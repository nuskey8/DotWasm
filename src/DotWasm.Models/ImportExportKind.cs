namespace DotWasm.Models;

public enum ImportExportKind : byte
{
    Function,
    Table,
    Memory,
    Global,
    Tag,
}
