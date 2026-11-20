using Acornima;
using Acornima.Ast;

namespace PageAssay.Static;

/// <summary>How a name was declared — what decides whether reading it early is <c>undefined</c> or throws.</summary>
internal enum BindingKind
{
    Var,
    Let,
    Const,
    Class,
    Function,
    Parameter,
    Import,
    CatchParameter,
    /// <summary>A function or class expression's own name, visible inside it.</summary>
    SelfName,
    /// <summary>A function declared inside a block, seen from outside the block: whether it has a value there depends on whether
    /// the block ran — never reported.</summary>
    BlockFunction,
}

internal enum ScopeKind
{
    Global,
    Module,
    Function,
    Block,
    Handler,
}

/// <summary>A piece of code the page runs: an inline script or an event-handler attribute.</summary>
internal sealed class CodeUnit(int index, Node ast, SourceSpan span, InlineScript? script, HandlerAttribute? handler)
{
    /// <summary>The unit's place in document order, scripts and handlers counted together.</summary>
    public int Index { get; } = index;
    public Node Ast { get; } = ast;
    public SourceSpan Span { get; } = span;
    public InlineScript? Script { get; } = script;
    public HandlerAttribute? Handler { get; } = handler;
    public bool Module => Script?.Module == true;
}

internal sealed class Binding(string name, BindingKind kind, Scope scope, CodeUnit unit, Identifier declaration)
{
    public string Name { get; } = name;
    public BindingKind Kind { get; } = kind;
    public Scope Scope { get; } = scope;
    public CodeUnit Unit { get; } = unit;
    public Identifier Declaration { get; } = declaration;

    /// <summary>The function the name holds, when the code says so where it declares it (a declaration, or a
    /// <c>const f = () => …</c>).</summary>
    public IFunction? Function { get; set; }

    /// <summary>Where the name first gets a value in the source — its declaration for <c>let</c>/<c>const</c>/<c>class</c>,
    /// the first initializer or assignment for <c>var</c>; null when nothing ever gives it one.</summary>
    public (CodeUnit Unit, Node Node)? SetAt { get; set; }
}

internal sealed class Scope(ScopeKind kind, Scope? parent, bool arrow = false)
{
    public ScopeKind Kind { get; } = kind;
    public Scope? Parent { get; } = parent;

    /// <summary>A function scope that is an arrow function's (no <c>arguments</c> of its own).</summary>
    public bool Arrow { get; } = arrow;

    public Dictionary<string, Binding> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>Where <c>var</c> declarations land.</summary>
    public bool IsVarScope => Kind is not ScopeKind.Block;

    public Binding? Lookup(string name)
    {
        for (var s = this; s is not null; s = s.Parent)
            if (s.Names.TryGetValue(name, out var binding)) return binding;
        return null;
    }

    public Scope VarScope()
    {
        var s = this;
        while (!s.IsVarScope) s = s.Parent!;
        return s;
    }
}

[Flags]
internal enum ReferenceUse
{
    Read = 1,
    Write = 2,
    Call = 4,
    TypeOf = 8,
}

internal sealed record Reference(Identifier Node, Scope Scope, CodeUnit Unit, Binding? Binding, ReferenceUse Use);

/// <summary>A call the rules look at: its callee and arguments, and where it is.</summary>
internal sealed record CallSite(Node Call, Expression Callee, IReadOnlyList<Node> Arguments, Scope Scope, CodeUnit Unit);

/// <summary>
/// The names of all the code a page runs and what each name refers to. Classic scripts share one global scope (their top-level
/// names are globals, visible to every later script and to handler attributes); a module script's top level is its own; a handler
/// attribute is a function whose scope reaches the globals.
/// </summary>
internal sealed class ScopeModel
{
    private readonly Dictionary<Node, Scope> _scopes = [];
    private readonly Dictionary<Identifier, Reference> _references = [];

    public ScopeModel(IReadOnlyList<CodeUnit> units)
    {
        Global = new Scope(ScopeKind.Global, null);
        foreach (var unit in units) new Walker(this, unit, declaring: true).Run();
        foreach (var unit in units) new Walker(this, unit, declaring: false).Run();
    }

    public Scope Global { get; }

    public List<Reference> References { get; } = [];
    public List<CallSite> Calls { get; } = [];

    /// <summary>Every binding of every scope, for telling a name that exists somewhere unreachable from one that exists nowhere.</summary>
    public List<Binding> Bindings { get; } = [];

    /// <summary>Names made global by assignment: <c>window.x = …</c>, or an assignment to a name declared nowhere.</summary>
    public HashSet<string> AssignedGlobals { get; } = new(StringComparer.Ordinal);

    /// <summary>Names some code tests with <c>typeof</c> or reads as <c>window.x</c>: the page knows they may be missing.</summary>
    public HashSet<string> TypeOfNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Ids the code gives elements it makes (<c>el.id = "x"</c>, <c>setAttribute("id", "x")</c>, <c>id="x"</c> in markup
    /// it inserts); a prefix when only the start is literal (<c>"row-" + n</c>).</summary>
    public IdsInCode Ids { get; } = new();

    /// <summary>Module specifiers: static <c>import</c>/<c>export … from</c>, and dynamic <c>import()</c> with a literal.</summary>
    public List<(Node Source, string Specifier, CodeUnit Unit)> Imports { get; } = [];

    /// <summary>The code uses <c>eval</c> or <c>with</c>: what a name refers to cannot be known from the source.</summary>
    public bool Dynamic { get; private set; }

    public Scope ScopeOf(Node node) => _scopes[node];

    public Reference? ReferenceOf(Identifier node) => _references.GetValueOrDefault(node);

    private sealed class Walker(ScopeModel model, CodeUnit unit, bool declaring)
    {
        public void Run()
        {
            Scope root = unit switch
            {
                { Handler: not null } => Scoped(unit.Ast, () => new Scope(ScopeKind.Handler, model.Global)),
                { Module: true } => Scoped(unit.Ast, () => new Scope(ScopeKind.Module, model.Global)),
                _ => model.Global,
            };
            if (unit.Ast is Acornima.Ast.Program program)
                foreach (var statement in program.Body) Visit(statement, root);
        }

        private Scope Scoped(Node node, Func<Scope> create)
        {
            if (declaring)
            {
                var scope = create();
                model._scopes[node] = scope;
                return scope;
            }
            return model._scopes[node];
        }

        private void Declare(Identifier id, BindingKind kind, Scope scope, IFunction? function = null)
        {
            if (!declaring) return;
            if (scope.Names.TryGetValue(id.Name, out var existing))
            {
                existing.Function ??= function;
                return;
            }
            var binding = new Binding(id.Name, kind, scope, unit, id) { Function = function };
            if (kind is BindingKind.Let or BindingKind.Const or BindingKind.Class or BindingKind.Function)
                binding.SetAt = (unit, id);
            scope.Names[id.Name] = binding;
            model.Bindings.Add(binding);
        }

        private void Visit(Node? node, Scope scope)
        {
            switch (node)
            {
                case null:
                    return;
                case Identifier id:
                    Use(id, scope, ReferenceUse.Read);
                    return;
                case StringLiteral s:
                    if (!declaring) model.Ids.FromMarkup(s.Value);
                    return;
                case TemplateElement t:
                    if (!declaring) model.Ids.FromMarkup(t.Value.Cooked ?? t.Value.Raw);
                    return;
                case FunctionDeclaration f:
                    DeclareFunction(f, scope);
                    VisitFunction(f, scope);
                    return;
                case FunctionExpression or ArrowFunctionExpression:
                    VisitFunction((IFunction)node, scope);
                    return;
                case ClassDeclaration c:
                    if (c.Id is not null) Declare(c.Id, BindingKind.Class, scope);
                    VisitClass(c, scope);
                    return;
                case ClassExpression c:
                    VisitClass(c, scope);
                    return;
                case VariableDeclaration v:
                    VisitDeclaration(v, scope);
                    return;
                case StaticBlock block:
                {
                    var inner = Scoped(block, () => new Scope(ScopeKind.Function, scope));
                    foreach (var statement in block.Body) Visit(statement, inner);
                    return;
                }
                case BlockStatement block:
                {
                    var inner = Scoped(block, () => new Scope(ScopeKind.Block, scope));
                    foreach (var statement in block.Body) Visit(statement, inner);
                    return;
                }
                case ForStatement f:
                {
                    var inner = Scoped(f, () => new Scope(ScopeKind.Block, scope));
                    Visit(f.Init, inner);
                    Visit(f.Test, inner);
                    Visit(f.Update, inner);
                    Visit(f.Body, inner);
                    return;
                }
                case ForInStatement f:
                    VisitForEach(f, f.Left, f.Right, f.Body, scope);
                    return;
                case ForOfStatement f:
                    VisitForEach(f, f.Left, f.Right, f.Body, scope);
                    return;
                case SwitchStatement s:
                {
                    Visit(s.Discriminant, scope);
                    var inner = Scoped(s, () => new Scope(ScopeKind.Block, scope));
                    foreach (var c in s.Cases)
                    {
                        Visit(c.Test, inner);
                        foreach (var statement in c.Consequent) Visit(statement, inner);
                    }
                    return;
                }
                case CatchClause c:
                {
                    var inner = Scoped(c, () => new Scope(ScopeKind.Block, scope));
                    if (c.Param is not null)
                    {
                        foreach (var name in PatternNames(c.Param)) Declare(name, BindingKind.CatchParameter, inner);
                        VisitPattern(c.Param, inner, declaration: true);
                    }
                    Visit(c.Body, inner);
                    return;
                }
                case MemberExpression m:
                    if (!declaring && m is { Computed: false, Object: Identifier { Name: "window" or "globalThis" or "self" } host, Property: Identifier probed }
                        && scope.Lookup(host.Name) is null)
                        model.TypeOfNames.Add(probed.Name);
                    Visit(m.Object, scope);
                    if (m.Computed) Visit(m.Property, scope);
                    return;
                case Property p:
                    if (p.Computed) Visit(p.Key, scope);
                    Visit(p.Value, scope);
                    return;
                case ClassProperty p:
                    if (p.Computed) Visit(p.Key, scope);
                    Visit(p.Value, scope);
                    return;
                case AssignmentExpression a:
                    VisitTarget(a.Left, scope, compound: a.Operator != Operator.Assignment);
                    if (!declaring)
                    {
                        NoteGlobalAssignment(a.Left, scope);
                        if (a.Left is MemberExpression { Computed: false, Property: Identifier { Name: "id" } }) model.Ids.FromValue(a.Right);
                    }
                    Visit(a.Right, scope);
                    return;
                case UpdateExpression u when u.Argument is Identifier id:
                    Use(id, scope, ReferenceUse.Read | ReferenceUse.Write);
                    return;
                case UnaryExpression { Operator: Operator.TypeOf, Argument: Identifier id }:
                    Use(id, scope, ReferenceUse.TypeOf);
                    return;
                case CallExpression c:
                    VisitCall(c, c.Callee, c.Arguments, scope);
                    return;
                case NewExpression n:
                    VisitCall(n, n.Callee, n.Arguments, scope);
                    return;
                case LabeledStatement l:
                    Visit(l.Body, scope);
                    return;
                case BreakStatement or ContinueStatement or MetaProperty or PrivateIdentifier:
                    return;
                case WithStatement w:
                    model.Dynamic = true;
                    Visit(w.Object, scope);
                    Visit(w.Body, scope);
                    return;
                case ImportDeclaration i:
                    foreach (var specifier in i.Specifiers)
                        if (specifier.Local is Identifier local) Declare(local, BindingKind.Import, scope);
                    NoteImport(i.Source);
                    return;
                case ExportNamedDeclaration e:
                    if (e.Declaration is not null) Visit(e.Declaration, scope);
                    else if (e.Source is null)
                        foreach (var specifier in e.Specifiers)
                            if (specifier.Local is Identifier local) Use(local, scope, ReferenceUse.Read);
                    if (e.Source is not null) NoteImport(e.Source);
                    return;
                case ExportAllDeclaration e:
                    NoteImport(e.Source);
                    return;
                case ImportExpression i:
                    if (i.Source is StringLiteral literal) NoteImport(literal);
                    Visit(i.Source, scope);
                    Visit(i.Options, scope);
                    return;
                default:
                    foreach (var child in node.ChildNodes) Visit(child, scope);
                    return;
            }
        }

        private void NoteImport(StringLiteral source)
        {
            if (!declaring) model.Imports.Add((source, source.Value, unit));
        }

        private void DeclareFunction(FunctionDeclaration f, Scope scope)
        {
            if (f.Id is null) return;
            if (scope.IsVarScope)
            {
                Declare(f.Id, BindingKind.Function, scope, f);
                return;
            }
            Declare(f.Id, BindingKind.Function, scope, f); // hoisted to the top of its block
            // Outside a module, a function declared in a block is also a var of the enclosing function once the block has run.
            if (!unit.Module) Declare(f.Id, BindingKind.BlockFunction, scope.VarScope(), f);
        }

        private void VisitFunction(IFunction f, Scope outer)
        {
            var inner = Scoped((Node)f, () => new Scope(ScopeKind.Function, outer, arrow: f is ArrowFunctionExpression));
            if (f is FunctionExpression { Id: { } self }) Declare(self, BindingKind.SelfName, inner, f);
            foreach (var parameter in f.Params)
            {
                foreach (var name in PatternNames(parameter)) Declare(name, BindingKind.Parameter, inner);
                VisitPattern(parameter, inner, declaration: true);
            }
            if (f.Body is FunctionBody body)
                foreach (var statement in body.Body) Visit(statement, inner);
            else
                Visit(f.Body, inner);
        }

        private void VisitClass(IClass c, Scope outer)
        {
            Visit(c.SuperClass, outer);
            var inner = Scoped((Node)c, () => new Scope(ScopeKind.Block, outer));
            if (c is ClassExpression { Id: { } self }) Declare(self, BindingKind.SelfName, inner);
            foreach (var decorator in c.Decorators) Visit(decorator, outer);
            foreach (var element in c.Body.Body) Visit(element, inner);
        }

        private void VisitDeclaration(VariableDeclaration v, Scope scope)
        {
            var kind = v.Kind switch
            {
                VariableDeclarationKind.Var => BindingKind.Var,
                VariableDeclarationKind.Const => BindingKind.Const,
                _ => BindingKind.Let,
            };
            var target = kind == BindingKind.Var ? scope.VarScope() : scope;
            foreach (var declarator in v.Declarations)
            {
                var function = declarator.Init as IFunction;
                foreach (var name in PatternNames(declarator.Id))
                {
                    Declare(name, kind, target, declarator.Id is Identifier ? function : null);
                    if (declaring && kind == BindingKind.Var && declarator.Init is not null
                        && target.Names.TryGetValue(name.Name, out var binding) && binding.SetAt is null)
                        binding.SetAt = (unit, name);
                }
                VisitPattern(declarator.Id, scope, declaration: true);
                Visit(declarator.Init, scope);
            }
        }

        private void VisitForEach(Node loop, Node left, Expression right, Statement body, Scope scope)
        {
            var inner = Scoped(loop, () => new Scope(ScopeKind.Block, scope));
            Visit(right, inner);
            if (left is VariableDeclaration v) VisitDeclaration(v, inner);
            else VisitTarget(left, inner, compound: false);
            Visit(body, inner);
        }

        private void VisitCall(Node call, Expression callee, in NodeList<Expression> arguments, Scope scope)
        {
            if (callee is Identifier id)
            {
                Use(id, scope, ReferenceUse.Read | ReferenceUse.Call);
                if (id.Name == "eval" && !declaring && scope.Lookup("eval") is null) model.Dynamic = true;
            }
            else
            {
                Visit(callee, scope);
            }
            var list = new List<Node>(arguments.Count);
            foreach (var argument in arguments)
            {
                list.Add(argument);
                Visit(argument, scope);
            }
            if (declaring) return;
            model.Calls.Add(new CallSite(call, callee, list, scope, unit));
            if (callee is MemberExpression { Property: Identifier { Name: "setAttribute" } } && list is [StringLiteral { Value: "id" }, Expression value, ..])
                model.Ids.FromValue(value);
        }

        private void VisitTarget(Node target, Scope scope, bool compound)
        {
            if (target is Identifier id)
                Use(id, scope, compound ? ReferenceUse.Read | ReferenceUse.Write : ReferenceUse.Write);
            else
                VisitPattern(target, scope, declaration: false);
        }

        // A destructuring pattern: its default values and computed keys are reads; its names are declarations or writes.
        private void VisitPattern(Node? pattern, Scope scope, bool declaration)
        {
            switch (pattern)
            {
                case null:
                    return;
                case Identifier id:
                    if (!declaration) Use(id, scope, ReferenceUse.Write);
                    return;
                case ObjectPattern o:
                    foreach (var property in o.Properties)
                        if (property is Property p)
                        {
                            if (p.Computed) Visit(p.Key, scope);
                            VisitPattern(p.Value, scope, declaration);
                        }
                        else
                        {
                            VisitPattern(property, scope, declaration);
                        }
                    return;
                case ArrayPattern a:
                    foreach (var element in a.Elements) VisitPattern(element, scope, declaration);
                    return;
                case AssignmentPattern a:
                    VisitPattern(a.Left, scope, declaration);
                    Visit(a.Right, scope);
                    return;
                case RestElement r:
                    VisitPattern(r.Argument, scope, declaration);
                    return;
                default:
                    Visit(pattern, scope); // a member expression as an assignment target
                    return;
            }
        }

        private void NoteGlobalAssignment(Node left, Scope scope)
        {
            if (left is MemberExpression { Object: Identifier { Name: "window" or "globalThis" or "self" } host } m
                && scope.Lookup(host.Name) is null)
            {
                if (!m.Computed && m.Property is Identifier property) model.AssignedGlobals.Add(property.Name);
                else if (m.Computed && m.Property is StringLiteral literal) model.AssignedGlobals.Add(literal.Value);
            }
        }

        private void Use(Identifier id, Scope scope, ReferenceUse use)
        {
            if (declaring) return;
            var binding = scope.Lookup(id.Name);
            if ((use & ReferenceUse.Write) != 0)
            {
                if (binding is null) model.AssignedGlobals.Add(id.Name);
                else if (binding.Kind == BindingKind.Var && (binding.SetAt is null || Before((unit, id), binding.SetAt.Value)))
                    binding.SetAt = (unit, id);
            }
            if ((use & ReferenceUse.TypeOf) != 0) model.TypeOfNames.Add(id.Name);
            var reference = new Reference(id, scope, unit, binding, use);
            model.References.Add(reference);
            model._references[id] = reference;
        }

        private static bool Before((CodeUnit Unit, Node Node) a, (CodeUnit Unit, Node Node) b) =>
            a.Unit.Index != b.Unit.Index ? a.Unit.Index < b.Unit.Index : a.Node.Start < b.Node.Start;
    }

    /// <summary>The names a pattern declares.</summary>
    public static IEnumerable<Identifier> PatternNames(Node? pattern) => pattern switch
    {
        Identifier id => [id],
        ObjectPattern o => o.Properties.SelectMany(p => p is Property property ? PatternNames(property.Value) : PatternNames(p)),
        ArrayPattern a => a.Elements.SelectMany(e => PatternNames(e)),
        AssignmentPattern a => PatternNames(a.Left),
        RestElement r => PatternNames(r.Argument),
        _ => [],
    };
}

/// <summary>The ids a page's code gives elements at run time, as far as the source says.</summary>
internal sealed partial class IdsInCode
{
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly List<string> _prefixes = [];

    /// <summary>Some code gives an id the source does not spell out: any id may exist at run time.</summary>
    public bool Unknown { get; private set; }

    public bool MayExist(string id) => Unknown || _exact.Contains(id) || _prefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal));

    public void FromValue(Expression value)
    {
        switch (value)
        {
            case StringLiteral s:
                _exact.Add(s.Value);
                break;
            case TemplateLiteral t when t.Expressions.Count == 0:
                _exact.Add(t.Quasis[0].Value.Cooked ?? "");
                break;
            case TemplateLiteral t when (t.Quasis[0].Value.Cooked ?? "").Length > 0:
                _prefixes.Add(t.Quasis[0].Value.Cooked!);
                break;
            case BinaryExpression { Operator: Operator.Addition, Left: var left } when Prefix(left) is { Length: > 0 } prefix:
                _prefixes.Add(prefix);
                break;
            default:
                Unknown = true;
                break;
        }
    }

    /// <summary>Markup in a string: every <c>id="…"</c> in it, or the start of one a template continues.</summary>
    public void FromMarkup(string text)
    {
        if (!text.Contains("id", StringComparison.Ordinal)) return;
        foreach (System.Text.RegularExpressions.Match m in IdAttribute().Matches(text))
        {
            if (m.Groups[2].Success) _exact.Add(m.Groups[1].Value);
            else if (m.Groups[1].Length > 0) _prefixes.Add(m.Groups[1].Value);
            else Unknown = true; // id="${…}": the whole id comes from an expression
        }
    }

    private static string? Prefix(Expression e) => e switch
    {
        StringLiteral s => s.Value,
        TemplateLiteral t => t.Quasis[0].Value.Cooked,
        BinaryExpression { Operator: Operator.Addition, Left: var left } => Prefix(left),
        _ => null,
    };

    // id="x" / id='x' / id=x, the value ending at its quote (group 2) or at the end of the string (a template continues it).
    [System.Text.RegularExpressions.GeneratedRegex("""\bid\s*=\s*["']?([^"'\s>]*)(["'\s>])?""")]
    private static partial System.Text.RegularExpressions.Regex IdAttribute();
}
