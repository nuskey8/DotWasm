namespace DotWasm.Validation;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using DotWasm.Models;

internal sealed class ValidationContext
{
    static readonly ConcurrentStack<ValidationContext> pool = new();

    public static ValidationContext Rent()
    {
        if (!pool.TryPop(out var context))
            context = new ValidationContext();
        return context;
    }

    public static void Return(ValidationContext context)
    {
        context.Reset();
        context.TypeResolver = null;
        context.TypeContext = null;
        context.TypeGraph = null;
        pool.Push(context);
    }

    public SubTypeResolver? TypeResolver { get; set; }
    public ImmutableArray<RecursiveType>? TypeContext { get; set; }
    public TypeGraph? TypeGraph { get; set; }

    sealed class Frame(
        WasmValueType[] startTypes,
        WasmValueType[] endResults,
        WasmValueType[] labelResults,
        int height,
        int initializedLocalHeight
    )
    {
        public WasmValueType[] StartTypes { get; } = startTypes;
        public WasmValueType[] EndResults { get; } = endResults;
        public WasmValueType[] LabelResults { get; } = labelResults;
        public int Height { get; } = height;
        public int InitializedLocalHeight { get; } = initializedLocalHeight;
        public bool IsIfWithoutElse { get; set; }
        public bool Unreachable { get; set; }
    }

    readonly Stack<WasmValueType> valueTypeStack = new();
    readonly Stack<Frame> frames = new();
    readonly Stack<int> initializedLocalStack = new();
    bool[] initializedLocals = [];

    Frame CurrentFrame => frames.Peek();

    public int LabelDepth => frames.Count - 1;

    public bool IsPolymorphic =>
        CurrentFrame.Unreachable && valueTypeStack.Count == CurrentFrame.Height;

    public bool IsUnreachable => CurrentFrame.Unreachable;

    public ReadOnlySpan<WasmValueType> GetLabelResults(uint labelIndex)
    {
        if (labelIndex >= frames.Count)
            WasmValidationException.Throw($"Label index {labelIndex} is out of bounds.");

        return frames.ElementAt(checked((int)labelIndex)).LabelResults;
    }

    public ReadOnlySpan<WasmValueType> GetFunctionResults()
    {
        return frames.Last().EndResults;
    }

    public void EnterFunction(
        ReadOnlySpan<WasmValueType> parameters,
        ReadOnlySpan<WasmValueType> locals,
        ReadOnlySpan<WasmValueType> results
    )
    {
        initializedLocals = new bool[parameters.Length + locals.Length];
        initializedLocals.AsSpan(0, parameters.Length).Fill(true);
        for (var i = 0; i < locals.Length; i++)
            initializedLocals[parameters.Length + i] = locals[i].IsDefaultable;

        var resultArray = results.ToArray();
        frames.Push(new Frame([], resultArray, resultArray, 0, initializedLocalStack.Count));
    }

    public void Push(WasmValueType type)
    {
        valueTypeStack.Push(type);
    }

    public WasmValueType Pop(WasmValueType expected)
    {
        if (CurrentFrame.Unreachable && valueTypeStack.Count == CurrentFrame.Height)
            return WasmTypes.Bottom;

        var actual = Pop();
        if (!actual.IsSubtypeOf(expected, TypeGraph))
            WasmValidationException.Throw($"Expected {expected} on the stack, found {actual}.");
        return actual;
    }

    public WasmValueType Pop()
    {
        if (valueTypeStack.Count == CurrentFrame.Height && CurrentFrame.Unreachable)
            return WasmTypes.Bottom;

        if (valueTypeStack.Count == CurrentFrame.Height)
            WasmValidationException.Throw("Instruction stack underflow.");

        return valueTypeStack.Pop();
    }

    public void MarkUnreachable()
    {
        while (valueTypeStack.Count > CurrentFrame.Height)
            valueTypeStack.Pop();
        CurrentFrame.Unreachable = true;
    }

    public void EnsureLocalInitialized(uint localIndex)
    {
        if (localIndex >= initializedLocals.Length)
            return;

        if (!initializedLocals[checked((int)localIndex)])
            WasmValidationException.Throw("Uninitialized local.");
    }

    public void InitializeLocal(uint localIndex)
    {
        if (localIndex >= initializedLocals.Length)
            return;

        var index = checked((int)localIndex);
        if (initializedLocals[index])
            return;

        initializedLocalStack.Push(index);
        initializedLocals[index] = true;
    }

    public void EnterLabel(
        ReadOnlySpan<WasmValueType> startTypes,
        ReadOnlySpan<WasmValueType> endResults,
        ReadOnlySpan<WasmValueType> labelResults
    )
    {
        var startArray = startTypes.ToArray();
        frames.Push(
            new Frame(
                startArray,
                endResults.ToArray(),
                labelResults.ToArray(),
                valueTypeStack.Count,
                initializedLocalStack.Count
            )
        );
        foreach (var type in startArray.AsSpan())
            Push(type);
    }

    public void MarkIfWithoutElse()
    {
        CurrentFrame.IsIfWithoutElse = true;
    }

    public void ElseLabel()
    {
        CurrentFrame.IsIfWithoutElse = false;
        var startTypes = CurrentFrame.StartTypes;
        var endResults = CurrentFrame.EndResults;
        var labelResults = CurrentFrame.LabelResults;
        EndFrame(pushResults: false);
        frames.Push(
            new Frame(
                startTypes,
                endResults,
                labelResults,
                valueTypeStack.Count,
                initializedLocalStack.Count
            )
        );
        foreach (var type in startTypes.AsSpan())
            Push(type);
    }

    public void ExitLabel()
    {
        EndFrame(pushResults: frames.Count > 1);
    }

    public void EnsureComplete()
    {
        if (frames.Count != 0)
            EndFrame(pushResults: false);
        if (frames.Count != 0)
            WasmValidationException.Throw("Control stack is not empty.");
        if (valueTypeStack.Count != 0)
            WasmValidationException.Throw("Instruction stack must be empty.");
    }

    public void EnsureEmpty()
    {
        if (valueTypeStack.Count != 0)
            WasmValidationException.Throw("Instruction stack must be empty.");
    }

    void EndFrame(bool pushResults)
    {
        var frame = CurrentFrame;
        if (
            frame.IsIfWithoutElse
            && !frame.StartTypes.AsSpan().SequenceEqual(frame.EndResults.AsSpan())
        )
            WasmValidationException.Throw("type mismatch");
        for (var i = frame.EndResults.Length - 1; i >= 0; i--)
            Pop(frame.EndResults[i]);

        if (valueTypeStack.Count != frame.Height)
            WasmValidationException.Throw("Instruction stack must match the control frame.");

        frames.Pop();
        while (initializedLocalStack.Count > frame.InitializedLocalHeight)
            initializedLocals[initializedLocalStack.Pop()] = false;

        if (!pushResults)
            return;

        foreach (var result in frame.EndResults.AsSpan())
            Push(result);
    }

    public void Reset()
    {
        valueTypeStack.Clear();
        frames.Clear();
        initializedLocalStack.Clear();
        initializedLocals = [];
    }
}
