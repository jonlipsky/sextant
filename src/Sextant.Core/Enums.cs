namespace Sextant.Core;

public enum SymbolKind
{
    Class,
    Interface,
    Struct,
    Enum,
    Delegate,
    Record,
    Method,
    Constructor,
    Property,
    Field,
    Event,
    Indexer,
    TypeParameter
}

public enum ReferenceKind
{
    Invocation,
    TypeRef,
    Attribute,
    Inheritance,
    Override,
    ObjectCreation
}

public enum RelationshipKind
{
    Implements,
    Inherits,
    Overrides,
    Instantiates,
    Returns,
    ParameterOf
}

public enum AccessKind
{
    Read,
    Write,
    ReadWrite
}

public enum Accessibility
{
    Public,
    Internal,
    Protected,
    Private,
    ProtectedInternal,
    PrivateProtected
}
