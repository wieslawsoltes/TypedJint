using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Expression = System.Linq.Expressions.Expression;
using TypedJint.Runtime;

namespace TypedJint;

public sealed class ExpressionTreeBackend
{
    private readonly IReadOnlyDictionary<string, object?> _globals;
    private readonly Dictionary<string, Expression> _symbols = new(StringComparer.Ordinal);
    private readonly List<ParameterExpression> _locals = new();
    private readonly Stack<LabelTarget> _breakTargets = new();
    private readonly Stack<LabelTarget> _continueTargets = new();
    public ExpressionTreeBackend(IReadOnlyDictionary<string, object?> globals) => _globals = globals;

    public Delegate Compile(JsFunctionDeclaration fn)
    {
        if (fn.Annotation is null) throw new InvalidOperationException("Function must be annotated.");
        var parameters = new List<ParameterExpression>();
        foreach (var name in fn.Parameters)
        {
            if (!fn.Annotation.Parameters.TryGetValue(name, out var type)) throw new NotSupportedException($"Parameter '{name}' has no JSDoc type.");
            var parameter = Expression.Parameter(type.ClrType, name);
            parameters.Add(parameter);
            _symbols[name] = parameter;
        }
        foreach (var global in _globals.Where(x => x.Value is not null)) _symbols[global.Key] = Expression.Constant(global.Value!, global.Value!.GetType());

        var expressions = new List<Expression>();
        var returnTarget = Expression.Label(fn.Annotation.ReturnType.ClrType, "return");
        foreach (var statement in fn.Body) expressions.Add(CompileStatement(statement, returnTarget));
        expressions.Add(Expression.Label(returnTarget, Default(fn.Annotation.ReturnType.ClrType)));
        var body = Expression.Block(_locals, expressions);
        var delegateType = fn.Annotation.ReturnType.ClrType == typeof(void) ? Expression.GetActionType(parameters.Select(x => x.Type).ToArray()) : Expression.GetFuncType(parameters.Select(x => x.Type).Append(fn.Annotation.ReturnType.ClrType).ToArray());
        return Expression.Lambda(delegateType, body, fn.Name, parameters).Compile();
    }

    private Expression CompileStatement(JsStatement statement, LabelTarget returnTarget) => statement switch
    {
        JsBlockStatement block => Expression.Block(block.Statements.Select(x => CompileStatement(x, returnTarget))),
        JsVariableStatement variable => AsVoid(CompileVariable(variable)),
        JsReturnStatement ret => Expression.Return(returnTarget, ret.Value is null ? Default(returnTarget.Type) : ConvertTo(CompileExpression(ret.Value), returnTarget.Type)),
        JsExpressionStatement expr => AsVoid(CompileExpression(expr.Expression)),

        JsIfStatement ifStatement => CompileIf(ifStatement, returnTarget),
        JsWhileStatement whileStatement => CompileWhile(whileStatement, returnTarget),
        JsForStatement forStatement => CompileFor(forStatement, returnTarget),
        JsBreakStatement => _breakTargets.Count == 0 ? throw new NotSupportedException("break can only be used inside a loop.") : Expression.Break(_breakTargets.Peek()),
        JsContinueStatement => _continueTargets.Count == 0 ? throw new NotSupportedException("continue can only be used inside a loop.") : Expression.Continue(_continueTargets.Peek()),
        JsThrowStatement throwStmt => Expression.Throw(Expression.New(typeof(Exception).GetConstructor(new[] { typeof(string) })!, Expression.Call(typeof(Convert).GetMethod("ToString", new[] { typeof(object) })!, Expression.Convert(CompileExpression(throwStmt.Value), typeof(object))))),
        JsTryStatement tryStmt => CompileTryCatchFinally(tryStmt, returnTarget),
        JsSwitchStatement switchStmt => CompileSwitch(switchStmt, returnTarget),
        _ => throw new NotSupportedException($"Unsupported statement '{statement.GetType().Name}'.")
    };

    private Expression CompileTryCatchFinally(JsTryStatement tryStmt, LabelTarget returnTarget)
    {
        var tryBody = CompileStatement(tryStmt.Block, returnTarget);
        CatchBlock? catchBlock = null;
        if (tryStmt.HandlerBlock is not null)
        {
            var exParam = Expression.Parameter(typeof(Exception), "__ex");
            var expressions = new List<Expression>();
            if (!string.IsNullOrEmpty(tryStmt.HandlerParam))
            {
                var msgLocal = Expression.Variable(typeof(string), tryStmt.HandlerParam);
                _locals.Add(msgLocal);
                _symbols[tryStmt.HandlerParam] = msgLocal;
                expressions.Add(Expression.Assign(msgLocal, Expression.Property(exParam, "Message")));
            }
            expressions.Add(CompileStatement(tryStmt.HandlerBlock, returnTarget));
            catchBlock = Expression.Catch(exParam, Expression.Block(expressions));
        }

        var finallyBody = tryStmt.Finalizer is not null ? CompileStatement(tryStmt.Finalizer, returnTarget) : null;
        if (catchBlock is not null && finallyBody is not null)
        {
            return Expression.TryCatchFinally(tryBody, finallyBody, catchBlock);
        }
        if (catchBlock is not null)
        {
            return Expression.TryCatch(tryBody, catchBlock);
        }
        if (finallyBody is not null)
        {
            return Expression.TryFinally(tryBody, finallyBody);
        }
        return tryBody;
    }

    private Expression CompileSwitch(JsSwitchStatement switchStmt, LabelTarget returnTarget)
    {
        var discVal = CompileExpression(switchStmt.Discriminant);
        var switchBreakTarget = Expression.Label("switch_break");
        _breakTargets.Push(switchBreakTarget);

        try
        {
            var cases = switchStmt.Cases;
            var useExpressionSwitch = discVal.Type != typeof(object);
            if (useExpressionSwitch)
            {
                foreach (var c in cases)
                {
                    if (c.Test != null && CompileExpression(c.Test).Type != discVal.Type)
                    {
                        useExpressionSwitch = false;
                        break;
                    }
                }
            }

            if (useExpressionSwitch)
            {
                var switchCases = new List<System.Linq.Expressions.SwitchCase>();
                Expression? defaultBody = null;
                foreach (var c in cases)
                {
                    var bodyExprs = c.Consequent.Select(stmt => CompileStatement(stmt, returnTarget)).ToList();
                    if (bodyExprs.Count == 0) bodyExprs.Add(Expression.Empty());
                    var body = Expression.Block(bodyExprs);

                    if (c.Test is not null)
                    {
                        var testVal = CompileExpression(c.Test);
                        switchCases.Add(Expression.SwitchCase(body, testVal));
                    }
                    else
                    {
                        defaultBody = body;
                    }
                }
                return Expression.Block(
                    Expression.Switch(discVal, defaultBody ?? Expression.Empty(), switchCases.ToArray()),
                    Expression.Label(switchBreakTarget)
                );
            }
            else
            {
                var tempDisc = Expression.Variable(discVal.Type, "__disc");
                _locals.Add(tempDisc);

                Expression resultExpr = Expression.Empty();
                Expression? defaultBody = null;
                
                for (int i = cases.Count - 1; i >= 0; i--)
                {
                    var c = cases[i];
                    var bodyExprs = c.Consequent.Select(stmt => CompileStatement(stmt, returnTarget)).ToList();
                    if (bodyExprs.Count == 0) bodyExprs.Add(Expression.Empty());
                    var body = Expression.Block(bodyExprs);

                    if (c.Test is null)
                    {
                        defaultBody = body;
                    }
                    else
                    {
                        var testVal = CompileExpression(c.Test);
                        var testExpr = CompileBinaryExpression("===", tempDisc, testVal);
                        if (resultExpr == Expression.Empty() && defaultBody != null)
                        {
                            resultExpr = Expression.IfThenElse(testExpr, body, defaultBody);
                        }
                        else
                        {
                            resultExpr = Expression.IfThenElse(testExpr, body, resultExpr);
                        }
                    }
                }

                if (resultExpr == Expression.Empty() && defaultBody != null)
                {
                    resultExpr = defaultBody;
                }

                return Expression.Block(
                    Expression.Assign(tempDisc, discVal),
                    resultExpr,
                    Expression.Label(switchBreakTarget)
                );
            }
        }
        finally
        {
            _breakTargets.Pop();
        }
    }

    private Expression CompileVariable(JsVariableStatement variable)
    {
        var value = CompileExpression(variable.Initializer);
        var local = Expression.Variable(value.Type, variable.Name);
        _locals.Add(local);
        _symbols[variable.Name] = local;
        return Expression.Assign(local, value);
    }

    private Expression CompileAssignmentExpression(JsAssignmentExpression assign)
    {
        if (assign.Target is JsMemberExpression member)
        {
            var inst = CompileExpression(member.Target);
            if (inst.Type == typeof(object))
            {
                var valExpr = CompileExpression(assign.Value);
                if (assign.Operator == "=")
                {
                    var setMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.SetProperty))!;
                    return Expression.Call(setMethod, inst, Expression.Constant(member.Member), Expression.Convert(valExpr, typeof(object)));
                }
                else
                {
                    var getMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.GetProperty))!;
                    var curVal = Expression.Call(getMethod, inst, Expression.Constant(member.Member));
                    var mOp = assign.Operator[..^1];
                    var mComputed = CompileBinaryExpression(mOp, curVal, valExpr);
                    var setMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.SetProperty))!;
                    return Expression.Call(setMethod, inst, Expression.Constant(member.Member), Expression.Convert(mComputed, typeof(object)));
                }
            }
        }
        else if (assign.Target is JsIndexExpression index)
        {
            var inst = CompileExpression(index.Target);
            if (inst.Type == typeof(object))
            {
                var idxExpr = CompileExpression(index.Index);
                var valExpr = CompileExpression(assign.Value);
                if (assign.Operator == "=")
                {
                    var setMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.SetIndex))!;
                    return Expression.Call(setMethod, inst, Expression.Convert(idxExpr, typeof(object)), Expression.Convert(valExpr, typeof(object)));
                }
                else
                {
                    var getMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.GetIndex))!;
                    var curVal = Expression.Call(getMethod, inst, Expression.Convert(idxExpr, typeof(object)));
                    var iOp = assign.Operator[..^1];
                    var iComputed = CompileBinaryExpression(iOp, curVal, valExpr);
                    var setMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.SetIndex))!;
                    return Expression.Call(setMethod, inst, Expression.Convert(idxExpr, typeof(object)), Expression.Convert(iComputed, typeof(object)));
                }
            }
        }

        var target = CompileAssignable(assign.Target);
        var val = CompileExpression(assign.Value);
        if (assign.Operator == "=")
        {
            return Expression.Assign(target, ConvertTo(val, target.Type));
        }

        var op = assign.Operator[..^1];
        var computed = CompileBinaryExpression(op, target, val);
        return Expression.Assign(target, ConvertTo(computed, target.Type));
    }

    private Expression CompileIf(JsIfStatement statement, LabelTarget returnTarget)
    {
        var test = ConvertTo(CompileExpression(statement.Test), typeof(bool));
        var consequent = CompileStatement(statement.Consequent, returnTarget);
        var alternate = statement.Alternate is null ? Expression.Empty() : CompileStatement(statement.Alternate, returnTarget);
        return Expression.IfThenElse(test, consequent, alternate);
    }

    private Expression CompileWhile(JsWhileStatement statement, LabelTarget returnTarget)
    {
        var breakTarget = Expression.Label("while_break");
        var continueTarget = Expression.Label("while_continue");
        _breakTargets.Push(breakTarget);
        _continueTargets.Push(continueTarget);
        try
        {
            return Expression.Loop(
                Expression.Block(
                    Expression.IfThen(Expression.Not(ConvertTo(CompileExpression(statement.Test), typeof(bool))), Expression.Break(breakTarget)),
                    CompileStatement(statement.Body, returnTarget),
                    Expression.Label(continueTarget)),
                breakTarget);
        }
        finally
        {
            _continueTargets.Pop();
            _breakTargets.Pop();
        }
    }

    private Expression CompileFor(JsForStatement statement, LabelTarget returnTarget)
    {
        var breakTarget = Expression.Label("for_break");
        var continueTarget = Expression.Label("for_continue");
        var expressions = new List<Expression>();
        if (statement.Init is not null) expressions.Add(CompileStatement(statement.Init, returnTarget));

        _breakTargets.Push(breakTarget);
        _continueTargets.Push(continueTarget);
        try
        {
            var loopExpressions = new List<Expression>();
            if (statement.Test is not null)
            {
                loopExpressions.Add(Expression.IfThen(Expression.Not(ConvertTo(CompileExpression(statement.Test), typeof(bool))), Expression.Break(breakTarget)));
            }

            loopExpressions.Add(CompileStatement(statement.Body, returnTarget));
            loopExpressions.Add(Expression.Label(continueTarget));
            if (statement.Update is not null) loopExpressions.Add(CompileStatement(statement.Update, returnTarget));
            expressions.Add(Expression.Loop(Expression.Block(loopExpressions), breakTarget));
            return Expression.Block(expressions);
        }
        finally
        {
            _continueTargets.Pop();
            _breakTargets.Pop();
        }
    }

    private Expression CompileExpression(JsExpression expression) => expression switch
    {
        JsLiteralExpression literal => CompileLiteral(literal.Value),
        JsIdentifierExpression identifier => _symbols.TryGetValue(identifier.Name, out var symbol) ? symbol : throw new NotSupportedException($"Unknown identifier '{identifier.Name}'."),
        JsMemberExpression member => CompileMember(member),
        JsIndexExpression index => CompileIndex(index),
        JsCallExpression call => CompileCall(call),
        JsBinaryExpression binary => CompileBinary(binary),
        JsUnaryExpression unary => CompileUnary(unary),
        JsUpdateExpression update => CompileUpdate(update),
        JsArrayExpression array => CompileArray(array),
        JsConditionalExpression conditional => CompileConditional(conditional),
        JsFunctionExpression func => CompileFunctionExpression(func),
        JsArrowFunctionExpression arrow => CompileArrowFunctionExpression(arrow),
        JsObjectExpression obj => CompileObjectExpression(obj),
        JsNewExpression newExpr => CompileNewExpression(newExpr),
        JsThisExpression => Expression.Constant(new object()),
        JsTemplateLiteralExpression temp => CompileTemplateLiteral(temp),
        JsAssignmentExpression assign => CompileAssignmentExpression(assign),
        _ => throw new NotSupportedException($"Unsupported expression '{expression.GetType().Name}'.")
    };

    private Expression CompileObjectExpression(JsObjectExpression obj)
    {
        var dictType = typeof(Dictionary<string, object?>);
        var ctor = dictType.GetConstructor(new[] { typeof(IEqualityComparer<string>) })!;
        var comparer = Expression.Constant(StringComparer.Ordinal);
        var dictVar = Expression.Variable(dictType, "dict");
        _locals.Add(dictVar);

        var expressions = new List<Expression>
        {
            Expression.Assign(dictVar, Expression.New(ctor, comparer))
        };

        var addMethod = dictType.GetMethod("Add")!;
        foreach (var p in obj.Properties)
        {
            var keyConst = Expression.Constant(p.Key);
            var valExpr = Expression.Convert(CompileExpression(p.Value), typeof(object));
            expressions.Add(Expression.Call(dictVar, addMethod, keyConst, valExpr));
        }

        expressions.Add(dictVar);
        return Expression.Block(expressions);
    }

    private Expression CompileNewExpression(JsNewExpression newExpr)
    {
        Type? type = null;
        if (_globals.TryGetValue(newExpr.Callee, out var val) && val is Type t)
        {
            type = t;
        }
        else if (newExpr.Callee == "Error")
        {
            type = typeof(Exception);
        }
        else if (newExpr.Callee == "Event")
        {
            type = typeof(DomEvent);
        }

        if (type is null)
        {
            return Expression.Constant(new object());
        }

        var argExprs = newExpr.Arguments.Select(arg => CompileExpression(arg)).ToList();
        var argTypes = argExprs.Select(x => x.Type).ToArray();
        var ctor = type.GetConstructor(argTypes);
        if (ctor is null)
        {
            ctor = type.GetConstructors().FirstOrDefault();
        }

        if (ctor is null) return Expression.Constant(new object());

        var ctorParams = ctor.GetParameters();
        var alignedArgs = new List<Expression>();
        for (int i = 0; i < ctorParams.Length; i++)
        {
            if (i < argExprs.Count)
            {
                alignedArgs.Add(ConvertTo(argExprs[i], ctorParams[i].ParameterType));
            }
            else
            {
                alignedArgs.Add(Default(ctorParams[i].ParameterType));
            }
        }

        return Expression.New(ctor, alignedArgs);
    }

    private Expression CompileTemplateLiteral(JsTemplateLiteralExpression temp)
    {
        var parts = new List<Expression>();
        var toStringMethod = typeof(Convert).GetMethod("ToString", new[] { typeof(object) })!;
        for (int i = 0; i < temp.Quasis.Count; i++)
        {
            var raw = temp.Quasis[i];
            if (!string.IsNullOrEmpty(raw))
            {
                parts.Add(Expression.Constant(raw));
            }
            if (i < temp.Expressions.Count)
            {
                var expr = CompileExpression(temp.Expressions[i]);
                parts.Add(Expression.Call(toStringMethod, Expression.Convert(expr, typeof(object))));
            }
        }
        if (parts.Count == 0) return Expression.Constant(string.Empty);
        var concatMethod = typeof(string).GetMethod("Concat", new[] { typeof(object), typeof(object) })!;
        Expression current = parts[0];
        for (int i = 1; i < parts.Count; i++)
        {
            current = Expression.Call(concatMethod, Expression.Convert(current, typeof(object)), Expression.Convert(parts[i], typeof(object)));
        }
        return current;
    }

    private Expression CompileFunctionExpression(JsFunctionExpression func)
    {
        var parameters = func.Parameters.Select(p => Expression.Parameter(typeof(object), p)).ToList();
        var nested = new ExpressionTreeBackend(_globals);
        foreach (var p in _symbols) nested._symbols[p.Key] = p.Value;
        foreach (var p in parameters) nested._symbols[p.Name!] = p;

        var bodyExprs = new List<Expression>();
        // Stub return label target for inner return statements
        var innerReturnTarget = Expression.Label(typeof(object), "inner_return");
        foreach (var stmt in func.Body)
        {
            bodyExprs.Add(nested.CompileStatement(stmt, innerReturnTarget));
        }
        bodyExprs.Add(Expression.Label(innerReturnTarget, Expression.Constant(null)));
        var block = Expression.Block(nested._locals, bodyExprs);
        return Expression.Lambda(block, parameters);
    }

    private Expression CompileArrowFunctionExpression(JsArrowFunctionExpression arrow)
    {
        var parameters = arrow.Parameters.Select(p => Expression.Parameter(typeof(object), p)).ToList();
        var nested = new ExpressionTreeBackend(_globals);
        foreach (var p in _symbols) nested._symbols[p.Key] = p.Value;
        foreach (var p in parameters) nested._symbols[p.Name!] = p;

        var bodyExprs = new List<Expression>();
        var innerReturnTarget = Expression.Label(typeof(object), "inner_return");
        foreach (var stmt in arrow.Body)
        {
            bodyExprs.Add(nested.CompileStatement(stmt, innerReturnTarget));
        }
        bodyExprs.Add(Expression.Label(innerReturnTarget, Expression.Constant(null)));
        var block = Expression.Block(nested._locals, bodyExprs);
        return Expression.Lambda(block, parameters);
    }

    private static Expression CompileLiteral(object? value) => value switch { null => Expression.Constant(null), double d => Expression.Constant(d), string s => Expression.Constant(s), bool b => Expression.Constant(b), _ => Expression.Constant(value) };
    private Expression CompileMember(JsMemberExpression member) => BindMember(CompileExpression(member.Target), member.Member);

    private Expression CompileAssignable(JsExpression expression) => expression switch
    {
        JsIdentifierExpression id when _symbols.TryGetValue(id.Name, out var symbol) => symbol,
        JsMemberExpression member => CompileMember(member),
        JsIndexExpression index => CompileIndex(index),
        _ => throw new NotSupportedException("Unsupported assignment target.")
    };

    private Expression CompileCall(JsCallExpression call)
    {
        if (call.Target is not JsMemberExpression member) throw new NotSupportedException("Only method calls are supported in phase one.");
        var instance = CompileExpression(member.Target);
        var args = call.Arguments.Select(CompileExpression).ToArray();
        
        if (instance.Type == typeof(object))
        {
            var invokeMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.InvokeMethod))!;
            var argsArrayExpr = Expression.NewArrayInit(typeof(object), args.Select(x => ConvertTo(x, typeof(object))));
            return Expression.Call(invokeMethod, instance, Expression.Constant(member.Member), argsArrayExpr);
        }

        var method = ResolveMethod(instance.Type, member.Member, args.Select(x => x.Type).ToArray());
        var converted = method.GetParameters().Select((p, i) => ConvertTo(args[i], p.ParameterType));
        return Expression.Call(instance, method, converted);
    }

    private Expression CompileArray(JsArrayExpression array)
    {
        var elements = array.Elements.Select(CompileExpression).ToArray();
        var elementType = InferArrayElementType(elements);
        return Expression.NewArrayInit(elementType, elements.Select(x => ConvertTo(x, elementType)));
    }

    private Expression CompileIndex(JsIndexExpression index)
    {
        var target = CompileExpression(index.Target);
        var indexExpression = CompileExpression(index.Index);
        if (target.Type == typeof(object))
        {
            var getMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.GetIndex))!;
            return Expression.Call(getMethod, target, Expression.Convert(indexExpression, typeof(object)));
        }
        if (target.Type.IsArray)
        {
            return Expression.ArrayAccess(target, ConvertTo(indexExpression, typeof(int)));
        }

        var indexer = target.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(x => x.GetIndexParameters().Length == 1 && CanConvert(indexExpression.Type, x.GetIndexParameters()[0].ParameterType));
        if (indexer is not null)
        {
            var parameter = indexer.GetIndexParameters()[0];
            return Expression.MakeIndex(target, indexer, new[] { ConvertTo(indexExpression, parameter.ParameterType) });
        }

        throw new NotSupportedException($"Index access is not supported for '{target.Type.Name}'.");
    }

    private Expression CompileConditional(JsConditionalExpression conditional)
    {
        var test = ConvertTo(CompileExpression(conditional.Test), typeof(bool));
        var consequent = CompileExpression(conditional.Consequent);
        var alternate = CompileExpression(conditional.Alternate);
        var type = CommonType(consequent.Type, alternate.Type);
        return Expression.Condition(test, ConvertTo(consequent, type), ConvertTo(alternate, type));
    }

    private static Expression BindMember(Expression target, string member)
    {
        if (target.Type == typeof(object))
        {
            var method = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.GetProperty))!;
            return Expression.Call(method, target, Expression.Constant(member));
        }
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var property = target.Type.GetProperty(member, flags);
        if (property is not null) return Expression.Property(target, property);
        var field = target.Type.GetField(member, flags);
        if (field is not null) return Expression.Field(target, field);
        throw new NotSupportedException($"Member '{member}' not found on '{target.Type.Name}'.");
    }

    private static MethodInfo ResolveMethod(Type type, string name, IReadOnlyList<Type> argumentTypes)
    {
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase).Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.GetParameters().Length == argumentTypes.Count))
        {
            var parameters = method.GetParameters();
            var ok = true;
            for (var i = 0; i < parameters.Length; i++) if (!CanConvert(argumentTypes[i], parameters[i].ParameterType)) { ok = false; break; }
            if (ok) return method;
        }
        throw new NotSupportedException($"Method '{name}' with {argumentTypes.Count} argument(s) not found on '{type.Name}'.");
    }

    private Expression CompileUnary(JsUnaryExpression unary)
    {
        var operand = CompileExpression(unary.Operand);
        return unary.Operator switch
        {
            "+" => ConvertTo(operand, typeof(double)),
            "-" => Expression.Negate(ConvertTo(operand, typeof(double))),
            "!" => Expression.Not(ConvertTo(operand, typeof(bool))),
            _ => throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported.")
        };
    }

    private Expression CompileUpdate(JsUpdateExpression update)
    {
        var target = CompileAssignable(update.Target);
        return update.Operator switch
        {
            "++" => update.Prefix ? Expression.PreIncrementAssign(target) : Expression.PostIncrementAssign(target),
            "--" => update.Prefix ? Expression.PreDecrementAssign(target) : Expression.PostDecrementAssign(target),
            _ => throw new NotSupportedException($"Update operator '{update.Operator}' is not supported.")
        };
    }

    private Expression CompileBinary(JsBinaryExpression binary)
    {
        var left = CompileExpression(binary.Left);
        var right = CompileExpression(binary.Right);
        if (binary.Operator == "||")
        {
            var method = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.LogicalOr))!;
            return Expression.Call(method, Expression.Convert(left, typeof(object)), Expression.Convert(right, typeof(object)));
        }
        if (binary.Operator == "&&")
        {
            var method = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.LogicalAnd))!;
            return Expression.Call(method, Expression.Convert(left, typeof(object)), Expression.Convert(right, typeof(object)));
        }
        if (binary.Operator == "??")
        {
            var method = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.Coalesce))!;
            return Expression.Call(method, Expression.Convert(left, typeof(object)), Expression.Convert(right, typeof(object)));
        }
        return CompileBinaryExpression(binary.Operator, left, right);
    }

    private static Expression CompileBinaryExpression(string op, Expression left, Expression right)
    {
        return op switch
        {
            "+" when left.Type == typeof(string) || right.Type == typeof(string) => Expression.Call(typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(object), typeof(object) })!, Expression.Convert(left, typeof(object)), Expression.Convert(right, typeof(object))),
            "+" => Expression.Add(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "-" => Expression.Subtract(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "*" => Expression.Multiply(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "/" => Expression.Divide(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "%" => Expression.Modulo(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "<" => Expression.LessThan(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "<=" => Expression.LessThanOrEqual(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            ">" => Expression.GreaterThan(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            ">=" => Expression.GreaterThanOrEqual(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "==" or "===" => Expression.Equal(ConvertComparable(left, right), ConvertComparable(right, left)),
            "!=" or "!==" => Expression.NotEqual(ConvertComparable(left, right), ConvertComparable(right, left)),
            "&&" => Expression.AndAlso(ConvertTo(left, typeof(bool)), ConvertTo(right, typeof(bool))),
            "||" => Expression.OrElse(ConvertTo(left, typeof(bool)), ConvertTo(right, typeof(bool))),
            _ => throw new NotSupportedException($"Operator '{op}' is not supported.")
        };
    }

    private static Expression ConvertComparable(Expression expression, Expression other)
    {
        if (expression.Type == other.Type) return expression;
        if (expression.Type == typeof(object)) return expression;
        if (other.Type == typeof(object)) return ConvertTo(expression, typeof(object));
        return ConvertTo(expression, other.Type);
    }

    private static Type InferArrayElementType(IReadOnlyList<Expression> elements)
    {
        if (elements.Count == 0) return typeof(object);
        var first = elements[0].Type;
        return elements.All(x => x.Type == first) ? first : typeof(object);
    }

    private static Type CommonType(Type left, Type right)
    {
        if (left == right) return left;
        if (left == typeof(double) && right == typeof(int) || left == typeof(int) && right == typeof(double)) return typeof(double);
        return typeof(object);
    }

    private static bool CanConvert(Type source, Type target) => target.IsAssignableFrom(source) || source == typeof(double) && target == typeof(int) || source == typeof(int) && target == typeof(double) || target == typeof(object);
    private static Expression ConvertTo(Expression expression, Type targetType) => expression.Type == targetType ? expression : Expression.Convert(expression, targetType);
    private static Expression Default(Type type) => type == typeof(void) ? Expression.Empty() : Expression.Default(type);
    private static Expression AsVoid(Expression expression) => expression.Type == typeof(void) ? expression : Expression.Block(expression, Expression.Empty());
}
