using Acornima;
using Acornima.Ast;

namespace PageAssay.Static;

/// <summary>A name read while the page starts, before the code that gives it a value has run. <c>Call</c> is the call made by the
/// starting code that led to the read (null when the starting code read it itself); <c>Throws</c> is true when the read throws
/// (<c>let</c>, <c>const</c>, <c>class</c>, or a name a later script declares), false when it gives <c>undefined</c> (a <c>var</c>).</summary>
internal sealed record EarlyRead(Binding Binding, Identifier Read, CodeUnit Unit, string? Call, bool Throws);

/// <summary>
/// Follows the code a page runs while it starts, in order, and notes every name read before it has a value. What it follows: the top
/// level of classic scripts in document order, then module scripts, then the <c>DOMContentLoaded</c> / <c>load</c> listeners and a
/// <c>&lt;body onload&gt;</c> handler registered by then; functions run at once (an IIFE) and calls to functions whose value the
/// source states. What it does not follow, so as never to report a read that does not happen: callbacks handed to other code
/// (timers, other events, array methods), an async function past its first <c>await</c>, generator bodies, and the branches of a
/// condition that tests <c>typeof</c> or <c>document.readyState</c>. Both branches of other conditions are followed.
/// </summary>
internal sealed class StartOrder
{
    private enum State
    {
        Unset, // a var: declared, undefined
        Dead,  // let / const / class before its declaration: reading throws
        Set,
    }

    private const int DepthLimit = 32;

    private readonly ScopeModel _model;
    private readonly Dictionary<Binding, State> _state = [];
    private readonly HashSet<Node> _active = [];
    private readonly HashSet<Binding> _initializing = [];
    private readonly List<(IFunction Function, string? Name)> _deferred = [];
    private string? _call;
    private bool _async;
    private bool _stopped;

    private StartOrder(ScopeModel model) => _model = model;

    public List<EarlyRead> Found { get; } = [];

    public static List<EarlyRead> Follow(ScopeModel model, IReadOnlyList<CodeUnit> units)
    {
        var order = new StartOrder(model);
        foreach (var unit in units.Where(u => u.Script is { Module: false }))
        {
            foreach (var binding in model.Global.Names.Values.Where(b => b.Unit == unit && !order._state.ContainsKey(b)))
                order.Initialize(binding);
            order.RunProgram(unit, model.Global, async: false);
        }
        foreach (var unit in units.Where(u => u.Module))
        {
            var scope = model.ScopeOf(unit.Ast);
            order.Enter(scope);
            order.RunProgram(unit, scope, async: true);
        }
        foreach (var unit in units.Where(u => u.Handler is { Event: "onload", Tag: "body" }))
        {
            var scope = model.ScopeOf(unit.Ast);
            order.Enter(scope);
            order.RunProgram(unit, scope, async: false);
        }
        for (var i = 0; i < order._deferred.Count; i++) // a listener may register another
            order.Invoke(order._deferred[i].Function, order._deferred[i].Name);
        return order.Found;
    }

    private void RunProgram(CodeUnit unit, Scope scope, bool async)
    {
        _async = async;
        _stopped = false;
        _call = null;
        if (unit.Ast is Acornima.Ast.Program program) RunList(program.Body, scope);
    }

    private void Initialize(Binding binding)
    {
        State? state = binding.Kind switch
        {
            BindingKind.Var => State.Unset,
            BindingKind.Let or BindingKind.Const or BindingKind.Class => State.Dead,
            BindingKind.BlockFunction => null,
            _ => State.Set,
        };
        if (state is { } s) _state[binding] = s;
    }

    private void Enter(Scope scope)
    {
        foreach (var binding in scope.Names.Values) Initialize(binding);
    }

    // Runs statements in order; stops at a return or throw of this list (one inside a condition may not happen).
    private void RunList(in NodeList<Statement> statements, Scope scope)
    {
        foreach (var statement in statements)
        {
            if (_stopped) return;
            if (Run(statement, scope)) return;
        }
    }

    /// <returns>True when the statement ends the list it is in (return, throw).</returns>
    private bool Run(Node? statement, Scope scope)
    {
        switch (statement)
        {
            case null:
                return false;
            case VariableDeclaration v:
                Declare(v, scope);
                return false;
            case FunctionDeclaration:
                return false;
            case ClassDeclaration c:
                Eval(c.SuperClass, scope);
                if (c.Id is not null) SetName(c.Id.Name, scope);
                return false;
            case ExpressionStatement e:
                Eval(e.Expression, scope);
                return false;
            case IfStatement i:
                Eval(i.Test, scope);
                if (!Guarded(i.Test))
                {
                    Run(i.Consequent, scope);
                    Run(i.Alternate, scope);
                }
                return false;
            case StaticBlock:
                return false;
            case BlockStatement b:
            {
                var inner = _model.ScopeOf(b);
                Enter(inner);
                RunList(b.Body, inner);
                return false;
            }
            case ForStatement f:
            {
                var inner = _model.ScopeOf(f);
                Enter(inner);
                if (f.Init is Expression init) Eval(init, inner);
                else Run(f.Init, inner);
                Eval(f.Test, inner);
                Run(f.Body, inner);
                Eval(f.Update, inner);
                return false;
            }
            case ForInStatement f:
                RunForEach(f, f.Left, f.Right, f.Body, scope);
                return false;
            case ForOfStatement f:
                RunForEach(f, f.Left, f.Right, f.Body, scope);
                return false;
            case WhileStatement w:
                Eval(w.Test, scope);
                Run(w.Body, scope);
                return false;
            case DoWhileStatement d:
                Run(d.Body, scope);
                Eval(d.Test, scope);
                return false;
            case TryStatement t:
                Run(t.Block, scope);
                if (t.Handler is { } handler)
                {
                    var inner = _model.ScopeOf(handler);
                    Enter(inner);
                    Run(handler.Body, inner);
                }
                Run(t.Finalizer, scope);
                return false;
            case SwitchStatement s:
            {
                Eval(s.Discriminant, scope);
                var inner = _model.ScopeOf(s);
                Enter(inner);
                foreach (var c in s.Cases)
                {
                    Eval(c.Test, inner);
                    RunList(c.Consequent, inner);
                }
                return false;
            }
            case ReturnStatement r:
                Eval(r.Argument, scope);
                return true;
            case ThrowStatement t:
                Eval(t.Argument, scope);
                return true;
            case LabeledStatement l:
                return Run(l.Body, scope);
            case ExportNamedDeclaration e:
                return Run(e.Declaration, scope);
            case ExportDefaultDeclaration e:
                if (e.Declaration is Expression expression) Eval(expression, scope);
                else Run(e.Declaration, scope);
                return false;
            default:
                return false;
        }
    }

    private void RunForEach(Node loop, Node left, Expression right, Statement body, Scope scope)
    {
        var inner = _model.ScopeOf(loop);
        Enter(inner);
        Eval(right, inner);
        Bind(left is VariableDeclaration v ? v.Declarations[0].Id : left, inner);
        Run(body, inner);
    }

    private void Declare(VariableDeclaration v, Scope scope)
    {
        foreach (var declarator in v.Declarations)
        {
            var names = ScopeModel.PatternNames(declarator.Id).Select(n => scope.Lookup(n.Name)).OfType<Binding>().ToList();
            foreach (var binding in names) _initializing.Add(binding);
            Eval(declarator.Init, scope);
            foreach (var binding in names) _initializing.Remove(binding);
            Bind(declarator.Id, scope);
        }
    }

    private void SetName(string name, Scope scope)
    {
        if (scope.Lookup(name) is { } binding && _state.ContainsKey(binding)) _state[binding] = State.Set;
    }

    // A destructuring pattern takes its values in order: a default may read a name bound earlier in the same pattern.
    private void Bind(Node? pattern, Scope scope)
    {
        switch (pattern)
        {
            case Identifier id:
                SetName(id.Name, scope);
                break;
            case AssignmentPattern a:
                Eval(a.Right, scope);
                Bind(a.Left, scope);
                break;
            case ObjectPattern o:
                foreach (var p in o.Properties)
                    if (p is Property property)
                    {
                        if (property.Computed) Eval(property.Key, scope);
                        Bind(property.Value, scope);
                    }
                    else Bind(p, scope);
                break;
            case ArrayPattern a:
                foreach (var e in a.Elements) Bind(e, scope);
                break;
            case RestElement r:
                Bind(r.Argument, scope);
                break;
            case MemberExpression m:
                Eval(m.Object, scope);
                if (m.Computed) Eval(m.Property, scope);
                break;
        }
    }

    private void Eval(Node? node, Scope scope)
    {
        if (node is null || _stopped) return;
        switch (node)
        {
            case Identifier id:
                Read(id);
                return;
            case UnaryExpression { Operator: Operator.TypeOf, Argument: Identifier }:
            case FunctionExpression or ArrowFunctionExpression or ClassExpression:
            case Literal or TemplateElement or ThisExpression or Super or MetaProperty:
                return;
            case AssignmentExpression a:
                Assign(a, scope);
                return;
            case CallExpression c:
                EvalCall(c, scope);
                return;
            case NewExpression n:
                Eval(n.Callee, scope);
                foreach (var argument in n.Arguments) Eval(argument, scope);
                return;
            case LogicalExpression l:
                Eval(l.Left, scope);
                if (!Guarded(l.Left)) Eval(l.Right, scope);
                return;
            case ConditionalExpression c:
                Eval(c.Test, scope);
                if (!Guarded(c.Test))
                {
                    Eval(c.Consequent, scope);
                    Eval(c.Alternate, scope);
                }
                return;
            case AwaitExpression a:
                Eval(a.Argument, scope);
                if (_async) _stopped = true; // the rest runs later, when the awaited value arrives
                return;
            case MemberExpression m:
                Eval(m.Object, scope);
                if (m.Computed) Eval(m.Property, scope);
                return;
            case Property p:
                if (p.Computed) Eval(p.Key, scope);
                Eval(p.Value, scope);
                return;
            default:
                foreach (var child in node.ChildNodes) Eval(child, scope);
                return;
        }
    }

    private void Assign(AssignmentExpression a, Scope scope)
    {
        if (a.Operator != Operator.Assignment && a.Left is Identifier read) Read(read);
        if (a.Left is MemberExpression target)
        {
            Eval(target.Object, scope);
            if (target.Computed) Eval(target.Property, scope);
        }
        Eval(a.Right, scope);
        if (a.Left is not MemberExpression) Bind(a.Left, scope);
        // window.onload = init / onload = () => …: runs after every script.
        var onload = a.Left switch
        {
            MemberExpression { Object: Identifier { Name: "window" }, Property: Identifier { Name: "onload" }, Computed: false } => true,
            Identifier { Name: "onload" } id => scope.Lookup(id.Name) is null,
            _ => false,
        };
        if (onload && FunctionOf(a.Right) is { } listener) _deferred.Add(listener);
    }

    private void EvalCall(CallExpression c, Scope scope)
    {
        var callee = Unwrap(c.Callee);
        switch (callee)
        {
            case Identifier id:
            {
                var ready = Read(id);
                foreach (var argument in c.Arguments) Eval(argument, scope);
                if (ready && _model.ReferenceOf(id)?.Binding?.Function is { } function) Invoke(function, id.Name);
                return;
            }
            case FunctionExpression or ArrowFunctionExpression:
                foreach (var argument in c.Arguments) Eval(argument, scope);
                Invoke((IFunction)callee, null);
                return;
            case MemberExpression m:
            {
                Eval(m.Object, scope);
                if (m.Computed) Eval(m.Property, scope);
                foreach (var argument in c.Arguments) Eval(argument, scope);
                if (m is { Computed: false, Property: Identifier { Name: "addEventListener" }, Object: Identifier { Name: "document" or "window" } host }
                    && scope.Lookup(host.Name) is null
                    && c.Arguments.Count >= 2 && c.Arguments[0] is StringLiteral { Value: "DOMContentLoaded" or "load" }
                    && FunctionOf(c.Arguments[1]) is { } listener)
                    _deferred.Add(listener);
                // f.call(…) / f.apply(…) runs f now.
                if (m is { Computed: false, Property: Identifier { Name: "call" or "apply" } } && FunctionOf(m.Object) is { } called)
                    Invoke(called.Function, called.Name);
                return;
            }
            default:
                Eval(callee, scope);
                foreach (var argument in c.Arguments) Eval(argument, scope);
                return;
        }
    }

    // The function an expression stands for, when the source says so: a function written in place, or a name holding one.
    private (IFunction Function, string? Name)? FunctionOf(Node expression) => Unwrap(expression) switch
    {
        FunctionExpression or ArrowFunctionExpression => ((IFunction)Unwrap(expression), null),
        Identifier id when _model.ReferenceOf(id)?.Binding is { Function: { } function } binding
                           && _state.GetValueOrDefault(binding, State.Unset) == State.Set => (function, id.Name),
        _ => null,
    };

    private static Node Unwrap(Node node) => node is ParenthesizedExpression p ? Unwrap(p.Expression) : node;

    private void Invoke(IFunction function, string? name)
    {
        var node = (Node)function;
        if (function.Generator || _active.Count >= DepthLimit || !_active.Add(node)) return;
        var (call, async, stopped) = (_call, _async, _stopped);
        _call ??= name;
        _async = function.Async;
        _stopped = false;
        var scope = _model.ScopeOf(node);
        Enter(scope);
        if (function.Body is FunctionBody body) RunList(body.Body, scope);
        else Eval(function.Body, scope);
        (_call, _async, _stopped) = (call, async, stopped); // an async function's await pauses it, not its caller
        _active.Remove(node);
    }

    /// <returns>True when the name has its value (a call through it can be followed).</returns>
    private bool Read(Identifier id)
    {
        if (_model.ReferenceOf(id) is not { Binding: { } binding } reference) return true;
        if (!_state.TryGetValue(binding, out var state))
        {
            if (binding.Scope.Kind != ScopeKind.Global || binding.Kind == BindingKind.BlockFunction) return true;
            // A name a later script declares: not there at all yet.
            Report(binding, id, reference.Unit, throws: true);
            return false;
        }
        switch (state)
        {
            case State.Set:
                return true;
            case State.Dead:
                Report(binding, id, reference.Unit, throws: true);
                return false;
            default:
                // var x = x || {} reads x on purpose; a var nothing ever assigns is meant to be undefined.
                if (!_initializing.Contains(binding) && binding.SetAt is not null) Report(binding, id, reference.Unit, throws: false);
                return false;
        }
    }

    private void Report(Binding binding, Identifier id, CodeUnit unit, bool throws)
    {
        if (binding.SetAt is null) return;
        Found.Add(new EarlyRead(binding, id, unit, _call == binding.Name ? null : _call, throws));
    }

    // A condition whose outcome the page decides at run time on purpose: it tests typeof, or document.readyState (an inline
    // script always runs while the document is loading, so «if loading, wait; else run now» never takes the second branch at start).
    private static bool Guarded(Node? node) => node switch
    {
        null => false,
        UnaryExpression { Operator: Operator.TypeOf } => true,
        MemberExpression { Computed: false, Property: Identifier { Name: "readyState" } } => true,
        FunctionExpression or ArrowFunctionExpression => false,
        _ => node.ChildNodes.Any(Guarded),
    };
}
