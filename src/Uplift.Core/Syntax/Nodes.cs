namespace Uplift.Syntax;

// Uplift's syntax tree for GS1 and GS2 (one tree: GS1 is read into the GS2 forms that mean the same). Nodes are immutable records;
// lists are never changed after a node is made (rewrites build new nodes). Compare trees with SyntaxEquality, not ==.

public abstract record Node;

public abstract record Expr : Node;

public abstract record Stmt : Node;

// ------------------------------------------------------------------ expressions

/// <summary>A number as written (12, 1.5, .25, 0x1F).</summary>
public sealed record NumberExpr(string Text) : Expr;

/// <summary>A text literal (its value, escapes resolved).</summary>
public sealed record StringExpr(string Value) : Expr;

public sealed record BoolExpr(bool Value) : Expr;

public sealed record NullExpr : Expr;

/// <summary>A name: a variable, an object (this, player, temp ...), or a word.</summary>
public sealed record NameExpr(string Name) : Expr;

/// <summary>target.name</summary>
public sealed record MemberExpr(Expr Target, string Name) : Expr;

/// <summary>target.(name), the member a value names.</summary>
public sealed record DynamicMemberExpr(Expr Target, Expr Name) : Expr;

/// <summary>target[index] or target[i, j].</summary>
public sealed record IndexExpr(Expr Target, List<Expr> Indices) : Expr;

/// <summary>name(args): a built-in function, or one of the script's own.</summary>
public sealed record CallExpr(string Name, List<Expr> Args) : Expr;

/// <summary>target.name(args)</summary>
public sealed record MethodCallExpr(Expr Target, string Name, List<Expr> Args) : Expr;

/// <summary>callee(args) where the callee is not a plain name: (expr)(args), target.(name)(args).</summary>
public sealed record DynamicCallExpr(Expr Callee, List<Expr> Args) : Expr;

/// <summary>Prefix -, +, !, ~, @ (to text), ++, --; or postfix ++, --.</summary>
public sealed record UnaryExpr(string Op, Expr Operand, bool Postfix = false) : Expr;

/// <summary>
///     Arithmetic, comparison, logic, text joining (@, and " ", "\n", "\t" for SPC, NL, TAB) and assignment (=, +=, ...). In a GS1
///     condition the parser writes the comparing = as "=" too; the converter makes it ==.
/// </summary>
public sealed record BinaryExpr(Expr Left, string Op, Expr Right) : Expr;

/// <summary>condition ? then : else</summary>
public sealed record ConditionalExpr(Expr Condition, Expr Then, Expr Else) : Expr;

/// <summary>value in &lt;low, high&gt; (a range: | inclusive, &lt; &gt; exclusive), or value in list (High null, Low the list).</summary>
public sealed record InExpr(Expr Value, Expr Low, Expr? High, bool LowExclusive, bool HighExclusive) : Expr;

/// <summary>{a, b, c}</summary>
public sealed record ArrayExpr(List<Expr> Items) : Expr;

/// <summary>new Type(args), or new (expr)(args) with the type a value names.</summary>
public sealed record NewExpr(string TypeName, List<Expr> Args, Expr? TypeExpr = null) : Expr;

/// <summary>a, b: each in turn (a statement of several expressions).</summary>
public sealed record SequenceExpr(List<Expr> Items) : Expr;

/// <summary>new[size] or new[a][b]</summary>
public sealed record NewArrayExpr(List<Expr> Sizes) : Expr;

/// <summary>function(params) { body } used as a value.</summary>
public sealed record FunctionExpr(List<string> Params, List<Stmt> Body) : Expr;

// ------------------------------------------------------------------ statements

public sealed record ExprStmt(Expr Expression) : Stmt;

public sealed record BlockStmt(List<Stmt> Body) : Stmt;

/// <summary>if (condition) then [else else]; an else-if is an Else of one IfStmt.</summary>
public sealed record IfStmt(Expr Condition, List<Stmt> Then, List<Stmt> Else, bool HasElse) : Stmt;

public sealed record WhileStmt(Expr Condition, List<Stmt> Body) : Stmt;

public sealed record DoWhileStmt(List<Stmt> Body, Expr Condition) : Stmt;

public sealed record ForStmt(Expr? Init, Expr? Condition, Expr? Step, List<Stmt> Body) : Stmt;

/// <summary>for (variable : source)</summary>
public sealed record ForEachStmt(Expr Variable, Expr Source, List<Stmt> Body) : Stmt;

public sealed record WithStmt(Expr Target, List<Stmt> Body) : Stmt;

public sealed record SwitchStmt(Expr Value, List<SwitchCase> Cases) : Stmt;

/// <summary>The labels of a case (null: default) and its statements.</summary>
public sealed record SwitchCase(List<Expr?> Labels, List<Stmt> Body);

public sealed record ReturnStmt(Expr? Value) : Stmt;

public sealed record BreakStmt : Stmt;

public sealed record ContinueStmt : Stmt;

/// <summary>new Type(args) { body }; (an object made with its members set: GUI controls). TypeExpr: new (expr)(args) { ... }.</summary>
public sealed record NewStmt(string TypeName, List<Expr> Args, List<Stmt> Body, Expr? TypeExpr = null) : Stmt;

/// <summary>A comment line the converter writes (never part of a parsed script).</summary>
public sealed record CommentStmt(string Text) : Stmt;

// ------------------------------------------------------------------ scripts

public abstract record Item : Node;

/// <summary>function [owner.]name(params) { body }, public or not.</summary>
public sealed record FunctionDecl(string Name, string? Owner, bool Public, List<string> Params, List<Stmt> Body) : Item;

/// <summary>A statement outside functions (main code).</summary>
public sealed record StatementItem(Stmt Statement) : Item;

/// <summary>const name = value;</summary>
public sealed record ConstDecl(string Name, Expr Value) : Item;

/// <summary>enum [name] { a, b = 2 }</summary>
public sealed record EnumDecl(string? Name, List<(string Name, Expr? Value)> Members) : Item;

/// <summary>A whole part: its functions and statements in order.</summary>
public sealed record Script(List<Item> Items) : Node;
