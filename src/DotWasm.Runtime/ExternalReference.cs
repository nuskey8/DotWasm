namespace DotWasm.Runtime;

public sealed class ExternalReference
{
    public required WasmValue Value { get; init; }
    public bool WrapsAnyReference { get; init; }

    public static ExternalReference Create(object? obj)
    {
        if (obj is null)
        {
            return new ExternalReference { Value = WasmValue.NullReference };
        }
        else if (obj is ExternalReference externRef)
        {
            return externRef;
        }
        else
        {
            return new ExternalReference
            {
                Value = WasmValue.FromRaw(obj),
                WrapsAnyReference = true,
            };
        }
    }
}
