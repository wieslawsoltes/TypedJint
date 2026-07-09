using System;
using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;

namespace TypedJint;

public static class JavaScriptTypeInferenceEngine
{
    private sealed class TypeTerm
    {
        public string? Name { get; set; }
        public JsStaticType? ConcreteType { get; set; }
        public TypeTerm? Parent { get; set; }

        public TypeTerm Find()
        {
            var current = this;
            while (current.Parent != null)
            {
                current = current.Parent;
            }
            return current;
        }

        public void Unify(TypeTerm other)
        {
            var root1 = this.Find();
            var root2 = other.Find();
            if (root1 == root2) return;

            if (root1.ConcreteType != null && root2.ConcreteType != null)
            {
                if (root1.ConcreteType != root2.ConcreteType)
                {
                    root1.ConcreteType = JsStaticType.Object;
                }
                root2.Parent = root1;
            }
            else if (root1.ConcreteType != null)
            {
                root2.Parent = root1;
            }
            else
            {
                root1.Parent = root2;
            }
        }
    }

    public static FunctionAnnotation Infer(FunctionDeclaration function)
    {
        var terms = new Dictionary<string, TypeTerm>(StringComparer.Ordinal);
        var returnTerm = new TypeTerm { ConcreteType = null };

        var paramNames = new List<string>();
        foreach (var p in function.Params)
        {
            if (p is Acornima.Ast.Identifier id)
            {
                paramNames.Add(id.Name);
                terms[id.Name] = new TypeTerm { Name = id.Name, ConcreteType = null };
            }
            else
            {
                var name = p.ToString() ?? "";
                paramNames.Add(name);
                terms[name] = new TypeTerm { Name = name, ConcreteType = null };
            }
        }

        var visitor = new InferenceVisitor(terms, returnTerm);
        visitor.Visit(function.Body);

        var inferredParams = new Dictionary<string, JsStaticType>(StringComparer.Ordinal);
        foreach (var name in paramNames)
        {
            var term = terms[name].Find();
            inferredParams[name] = term.ConcreteType ?? JsStaticType.Number;
        }

        var inferredReturn = returnTerm.Find().ConcreteType ?? JsStaticType.Void;

        return new FunctionAnnotation(inferredParams, inferredReturn);
    }

    private sealed class InferenceVisitor : AstVisitor
    {
        private readonly Dictionary<string, TypeTerm> _terms;
        private readonly TypeTerm _returnTerm;

        public InferenceVisitor(Dictionary<string, TypeTerm> terms, TypeTerm returnTerm)
        {
            _terms = terms;
            _returnTerm = returnTerm;
        }

        private TypeTerm GetOrRegisterTerm(string name)
        {
            if (!_terms.TryGetValue(name, out var term))
            {
                term = new TypeTerm { Name = name };
                _terms[name] = term;
            }
            return term;
        }

        private TypeTerm GetExpressionTerm(Acornima.Ast.Expression? expr)
        {
            if (expr is null) return new TypeTerm { ConcreteType = JsStaticType.Object };

            switch (expr)
            {
                case Acornima.Ast.Literal lit:
                    if (lit is NumericLiteral) return new TypeTerm { ConcreteType = JsStaticType.Number };
                    if (lit is StringLiteral) return new TypeTerm { ConcreteType = JsStaticType.String };
                    if (lit is BooleanLiteral) return new TypeTerm { ConcreteType = JsStaticType.Boolean };
                    return new TypeTerm { ConcreteType = JsStaticType.Object };

                case Acornima.Ast.Identifier id:
                    return GetOrRegisterTerm(id.Name);

                case Acornima.Ast.LogicalExpression log:
                    var lLogTerm = GetExpressionTerm(log.Left);
                    _ = GetExpressionTerm(log.Right);
                    if (log.Operator == Operator.NullishCoalescing)
                    {
                        lLogTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Object });
                    }
                    return new TypeTerm();

                case Acornima.Ast.BinaryExpression bin:
                    var leftTerm = GetExpressionTerm(bin.Left);
                    var rightTerm = GetExpressionTerm(bin.Right);
                    var resultTerm = new TypeTerm();

                    var op = bin.Operator;
                    if (op == Operator.Addition || op == Operator.Subtraction || op == Operator.Multiplication ||
                        op == Operator.Division || op == Operator.Remainder || op == Operator.BitwiseAnd ||
                        op == Operator.BitwiseOr || op == Operator.BitwiseXor || op == Operator.LeftShift ||
                        op == Operator.RightShift || op == Operator.UnsignedRightShift)
                    {
                        leftTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Number });
                        rightTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Number });
                        resultTerm.ConcreteType = JsStaticType.Number;
                    }
                    else if (op == Operator.LessThan || op == Operator.GreaterThan || op == Operator.LessThanOrEqual ||
                             op == Operator.GreaterThanOrEqual || op == Operator.Equality || op == Operator.Inequality ||
                             op == Operator.StrictEquality || op == Operator.StrictInequality)
                    {
                        var lType = leftTerm.Find().ConcreteType;
                        var rType = rightTerm.Find().ConcreteType;
                        if (lType != null && rType == null) rightTerm.Unify(leftTerm);
                        else if (rType != null && lType == null) leftTerm.Unify(rightTerm);

                        resultTerm.ConcreteType = JsStaticType.Boolean;
                    }
                    else
                    {
                        resultTerm.ConcreteType = JsStaticType.Object;
                    }
                    return resultTerm;

                case Acornima.Ast.UpdateExpression upd:
                    var updArg = GetExpressionTerm(upd.Argument);
                    updArg.Unify(new TypeTerm { ConcreteType = JsStaticType.Number });
                    return new TypeTerm { ConcreteType = JsStaticType.Number };

                case Acornima.Ast.UnaryExpression un:
                    var argTerm = GetExpressionTerm(un.Argument);
                    if (un.Operator == Operator.LogicalNot)
                    {
                        return new TypeTerm { ConcreteType = JsStaticType.Boolean };
                    }
                    if (un.Operator == Operator.UnaryPlus || un.Operator == Operator.UnaryNegation || un.Operator == Operator.BitwiseNot)
                    {
                        argTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Number });
                        return new TypeTerm { ConcreteType = JsStaticType.Number };
                    }
                    if (un.Operator == Operator.TypeOf)
                    {
                        return new TypeTerm { ConcreteType = JsStaticType.String };
                    }
                    return new TypeTerm { ConcreteType = JsStaticType.Object };

                case Acornima.Ast.AssignmentExpression assign:
                    var target = GetExpressionTerm(assign.Left as Acornima.Ast.Expression);
                    var val = GetExpressionTerm(assign.Right);
                    target.Unify(val);
                    return target;

                case Acornima.Ast.CallExpression call:
                    if (call.Callee is Acornima.Ast.MemberExpression mem && mem.Object is Acornima.Ast.Identifier idObj && idObj.Name == "Math")
                    {
                        return new TypeTerm { ConcreteType = JsStaticType.Number };
                    }
                    if (call.Callee is Acornima.Ast.Identifier idCall)
                    {
                        if (idCall.Name == "fetch") return new TypeTerm { ConcreteType = JsStaticType.Object };
                        if (idCall.Name == "setTimeout" || idCall.Name == "setInterval") return new TypeTerm { ConcreteType = JsStaticType.Number };
                    }
                    return new TypeTerm { ConcreteType = JsStaticType.Object };

                case Acornima.Ast.ConditionalExpression cond:
                    var test = GetExpressionTerm(cond.Test);
                    test.Unify(new TypeTerm { ConcreteType = JsStaticType.Boolean });
                    var cons = GetExpressionTerm(cond.Consequent);
                    var alt = GetExpressionTerm(cond.Alternate);
                    cons.Unify(alt);
                    return cons;

                default:
                    return new TypeTerm { ConcreteType = JsStaticType.Object };
            }
        }

        protected override object? VisitVariableDeclarator(VariableDeclarator node)
        {
            if (node.Id is Acornima.Ast.Identifier id)
            {
                var varTerm = GetOrRegisterTerm(id.Name);
                if (node.Init != null)
                {
                    var initTerm = GetExpressionTerm(node.Init);
                    varTerm.Unify(initTerm);
                }
            }
            return base.VisitVariableDeclarator(node);
        }

        protected override object? VisitReturnStatement(ReturnStatement node)
        {
            if (node.Argument != null)
            {
                var retTerm = GetExpressionTerm(node.Argument);
                _returnTerm.Unify(retTerm);
            }
            else
            {
                _returnTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Void });
            }
            return base.VisitReturnStatement(node);
        }

        protected override object? VisitExpressionStatement(ExpressionStatement node)
        {
            GetExpressionTerm(node.Expression);
            return base.VisitExpressionStatement(node);
        }

        protected override object? VisitIfStatement(IfStatement node)
        {
            var testTerm = GetExpressionTerm(node.Test);
            testTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Boolean });
            return base.VisitIfStatement(node);
        }

        protected override object? VisitWhileStatement(WhileStatement node)
        {
            var testTerm = GetExpressionTerm(node.Test);
            testTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Boolean });
            return base.VisitWhileStatement(node);
        }

        protected override object? VisitForStatement(ForStatement node)
        {
            if (node.Test != null)
            {
                var testTerm = GetExpressionTerm(node.Test);
                testTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Boolean });
            }
            return base.VisitForStatement(node);
        }
    }
}
