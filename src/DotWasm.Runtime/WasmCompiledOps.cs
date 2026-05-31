using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DotWasm.Models;

namespace DotWasm.Runtime;

internal sealed partial class WasmExecutionContext
{
    interface ILiteral<T>
    {
        abstract static T Value { get; }
    }

    interface IHex
    {
        abstract static int Value { get; }
    }

    readonly struct Hex0 : IHex
    {
        public static int Value => 0;
    }

    readonly struct Hex1 : IHex
    {
        public static int Value => 1;
    }

    readonly struct Hex2 : IHex
    {
        public static int Value => 2;
    }

    readonly struct Hex3 : IHex
    {
        public static int Value => 3;
    }

    readonly struct Hex4 : IHex
    {
        public static int Value => 4;
    }

    readonly struct Hex5 : IHex
    {
        public static int Value => 5;
    }

    readonly struct Hex6 : IHex
    {
        public static int Value => 6;
    }

    readonly struct Hex7 : IHex
    {
        public static int Value => 7;
    }

    readonly struct Hex8 : IHex
    {
        public static int Value => 8;
    }

    readonly struct Hex9 : IHex
    {
        public static int Value => 9;
    }

    readonly struct HexA : IHex
    {
        public static int Value => 10;
    }

    readonly struct HexB : IHex
    {
        public static int Value => 11;
    }

    readonly struct HexC : IHex
    {
        public static int Value => 12;
    }

    readonly struct HexD : IHex
    {
        public static int Value => 13;
    }

    readonly struct HexE : IHex
    {
        public static int Value => 14;
    }

    readonly struct HexF : IHex
    {
        public static int Value => 15;
    }

    readonly struct Int<H7, H6, H5, H4, H3, H2, H1, H0> : ILiteral<int>
        where H7 : IHex
        where H6 : IHex
        where H5 : IHex
        where H4 : IHex
        where H3 : IHex
        where H2 : IHex
        where H1 : IHex
        where H0 : IHex
    {
        public static int Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get =>
                H7.Value << 28
                | H6.Value << 24
                | H5.Value << 20
                | H4.Value << 16
                | H3.Value << 12
                | H2.Value << 8
                | H1.Value << 4
                | H0.Value;
        }
    }

    readonly struct Long<H15, H14, H13, H12, H11, H10, H9, H8, H7, H6, H5, H4, H3, H2, H1, H0>
        : ILiteral<long>
        where H15 : IHex
        where H14 : IHex
        where H13 : IHex
        where H12 : IHex
        where H11 : IHex
        where H10 : IHex
        where H9 : IHex
        where H8 : IHex
        where H7 : IHex
        where H6 : IHex
        where H5 : IHex
        where H4 : IHex
        where H3 : IHex
        where H2 : IHex
        where H1 : IHex
        where H0 : IHex
    {
        public static long Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get =>
                (long)(
                    (ulong)(uint)H15.Value << 60
                    | (ulong)(uint)H14.Value << 56
                    | (ulong)(uint)H13.Value << 52
                    | (ulong)(uint)H12.Value << 48
                    | (ulong)(uint)H11.Value << 44
                    | (ulong)(uint)H10.Value << 40
                    | (ulong)(uint)H9.Value << 36
                    | (ulong)(uint)H8.Value << 32
                    | (ulong)(uint)H7.Value << 28
                    | (ulong)(uint)H6.Value << 24
                    | (ulong)(uint)H5.Value << 20
                    | (ulong)(uint)H4.Value << 16
                    | (ulong)(uint)H3.Value << 12
                    | (ulong)(uint)H2.Value << 8
                    | (ulong)(uint)H1.Value << 4
                    | (ulong)(uint)H0.Value
                );
        }
    }

    readonly struct Float<H7, H6, H5, H4, H3, H2, H1, H0> : ILiteral<float>
        where H7 : IHex
        where H6 : IHex
        where H5 : IHex
        where H4 : IHex
        where H3 : IHex
        where H2 : IHex
        where H1 : IHex
        where H0 : IHex
    {
        public static float Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.BitCast<int, float>(Int<H7, H6, H5, H4, H3, H2, H1, H0>.Value);
        }
    }

    readonly struct Double<
        H15,
        H14,
        H13,
        H12,
        H11,
        H10,
        H9,
        H8,
        H7,
        H6,
        H5,
        H4,
        H3,
        H2,
        H1,
        H0
    > : ILiteral<double>
        where H15 : IHex
        where H14 : IHex
        where H13 : IHex
        where H12 : IHex
        where H11 : IHex
        where H10 : IHex
        where H9 : IHex
        where H8 : IHex
        where H7 : IHex
        where H6 : IHex
        where H5 : IHex
        where H4 : IHex
        where H3 : IHex
        where H2 : IHex
        where H1 : IHex
        where H0 : IHex
    {
        public static double Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.BitCast<long, double>(
                Long<H15, H14, H13, H12, H11, H10, H9, H8, H7, H6, H5, H4, H3, H2, H1, H0>.Value
            );
        }
    }

    static class TypeLiteralFactory
    {
        static readonly Type[] HexTypes =
        [
            typeof(Hex0),
            typeof(Hex1),
            typeof(Hex2),
            typeof(Hex3),
            typeof(Hex4),
            typeof(Hex5),
            typeof(Hex6),
            typeof(Hex7),
            typeof(Hex8),
            typeof(Hex9),
            typeof(HexA),
            typeof(HexB),
            typeof(HexC),
            typeof(HexD),
            typeof(HexE),
            typeof(HexF),
        ];

        public static Type CreateInt32(int value) =>
            typeof(Int<,,,,,,,>).MakeGenericType(CreateHexTypes(unchecked((uint)value), 8));

        public static Type CreateInt64(long value) =>
            typeof(Long<,,,,,,,,,,,,,,,>).MakeGenericType(
                CreateHexTypes(unchecked((ulong)value), 16)
            );

        public static Type CreateFloat32(float value) =>
            typeof(Float<,,,,,,,>).MakeGenericType(
                CreateHexTypes(Unsafe.BitCast<float, uint>(value), 8)
            );

        public static Type CreateFloat64(double value) =>
            typeof(Double<,,,,,,,,,,,,,,,>).MakeGenericType(
                CreateHexTypes(Unsafe.BitCast<double, ulong>(value), 16)
            );

        static Type[] CreateHexTypes(ulong value, int count)
        {
            var typeArgs = new Type[count];
            for (var i = 0; i < count; i++)
            {
                var shift = (count - 1 - i) * 4;
                typeArgs[i] = HexTypes[(int)((value >>> shift) & 0xF)];
            }
            return typeArgs;
        }
    }

    interface IBranchTable
    {
        abstract static int Get(int index);
    }

    readonly struct BranchTableDefault<TDefault> : IBranchTable
        where TDefault : ILiteral<int>
    {
        public static int Get(int index) => TDefault.Value;
    }

    readonly struct BranchTableNode<TLabel, TNext> : IBranchTable
        where TLabel : ILiteral<int>
        where TNext : IBranchTable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Get(int index) => index == 0 ? TLabel.Value : TNext.Get(index - 1);
    }

    static class BranchTableTypeFactory
    {
        public static Type Create(BrTableInstruction instruction)
        {
            var type = typeof(BranchTableDefault<>).MakeGenericType(
                TypeLiteralFactory.CreateInt32((int)instruction.DefaultLabelIndex)
            );
            for (var i = instruction.LabelIndices.Length - 1; i >= 0; i--)
            {
                type = typeof(BranchTableNode<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)instruction.LabelIndices[i]),
                    type
                );
            }
            return type;
        }
    }

    readonly struct WasmNop<TNext> : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame) =>
            TNext.Run(context, ref frame);
    }

    readonly struct WasmUnreachable<TNext> : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            WasmTrapException.Throw("Unreachable code executed.");
            return default;
        }
    }

    readonly struct WasmBr<TLabelIndex, TNext> : IWasmCompiledOp
        where TLabelIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame) =>
            WasmOpResult.Branch(TLabelIndex.Value);
    }

    readonly struct WasmBrIf<TLabelIndex, TNext> : IWasmCompiledOp
        where TLabelIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var condition = context.valueStack.UnsafePop().I32;
            return condition != 0 ? WasmOpResult.Branch(TLabelIndex.Value) : TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmBrTable<TBranchTable, TNext> : IWasmCompiledOp
        where TBranchTable : IBranchTable
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var selector = context.valueStack.UnsafePop().I32;
            return WasmOpResult.Branch(TBranchTable.Get(selector));
        }
    }

    readonly struct WasmReturn<TNext> : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            context.PreserveValues(frame.FunctionStackBase, frame.FunctionResultCount);
            return WasmOpResult.Return;
        }
    }

    readonly struct WasmCall<TFunctionIndex, TNext> : IWasmCompiledOp
        where TFunctionIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            context.ExecuteFunction(frame.Instance, frame.Instance.GetFunction(TFunctionIndex.Value));
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmCallIndirect<TTypeIndex, TTableIndex, TNext> : IWasmCompiledOp
        where TTypeIndex : ILiteral<int>
        where TTableIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var callee = context.ResolveIndirectFunction(
                frame.Instance,
                (uint)TTableIndex.Value,
                (uint)TTypeIndex.Value
            );
            context.ExecuteFunction(frame.Instance, callee);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmReturnCall<TFunctionIndex, TNext> : IWasmCompiledOp
        where TFunctionIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame) =>
            WasmOpResult.Tail(frame.Instance.GetFunction(TFunctionIndex.Value));
    }

    readonly struct WasmReturnCallIndirect<TTypeIndex, TTableIndex, TNext> : IWasmCompiledOp
        where TTypeIndex : ILiteral<int>
        where TTableIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame) =>
            WasmOpResult.Tail(
                context.ResolveIndirectFunction(frame.Instance, (uint)TTableIndex.Value, (uint)TTypeIndex.Value)
            );
    }

    readonly struct WasmCallRef<TTypeIndex, TNext> : IWasmCompiledOp
        where TTypeIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            context.ExecuteFunction(
                frame.Instance,
                context.ResolveFunctionReference(frame.Instance, (uint)TTypeIndex.Value)
            );
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmReturnCallRef<TTypeIndex, TNext> : IWasmCompiledOp
        where TTypeIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame) =>
            WasmOpResult.Tail(context.ResolveFunctionReference(frame.Instance, (uint)TTypeIndex.Value));
    }

    readonly struct WasmDrop<TNext> : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            context.valueStack.UnsafePop();
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmSelect<TNext> : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var condition = context.valueStack.UnsafePop();
            var value2 = context.valueStack.UnsafePop();
            var value1 = context.valueStack.UnsafePop();
            context.valueStack.Push(condition.I32 != 0 ? value1 : value2);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmLocalGet<TLocalIndex, TNext> : IWasmCompiledOp
        where TLocalIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            context.valueStack.Push(frame.Locals[TLocalIndex.Value]);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmLocalSet<TLocalIndex, TNext> : IWasmCompiledOp
        where TLocalIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            frame.Locals[TLocalIndex.Value] = context.valueStack.UnsafePop();
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmLocalTee<TLocalIndex, TNext> : IWasmCompiledOp
        where TLocalIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var value = context.valueStack.UnsafePop();
            frame.Locals[TLocalIndex.Value] = value;
            context.valueStack.Push(value);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmGlobalGet<TGlobalIndex, TNext> : IWasmCompiledOp
        where TGlobalIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            context.valueStack.Push(frame.Instance.GetGlobalInstance(TGlobalIndex.Value).Value);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmGlobalSet<TGlobalIndex, TNext> : IWasmCompiledOp
        where TGlobalIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            frame.Instance.GetGlobalInstance(TGlobalIndex.Value).Value = context.valueStack.UnsafePop();
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmTableGet<TTableIndex, TNext> : IWasmCompiledOp
        where TTableIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var table = frame.Instance.GetTableInstance(TTableIndex.Value);
            var index = context.PopTableIndex(table);
            if (index < 0 || table.References.Length <= index)
                WasmTrapException.Throw($"Invalid table index: {index}");
            context.valueStack.Push(table.References[index]);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmTableSet<TTableIndex, TNext> : IWasmCompiledOp
        where TTableIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var table = frame.Instance.GetTableInstance(TTableIndex.Value);
            var reference = context.valueStack.UnsafePop();
            var index = context.PopTableIndex(table);
            if (index < 0 || table.References.Length <= index)
                WasmTrapException.Throw($"Invalid table index: {index}");
            table.References[index] = reference;
            return TNext.Run(context, ref frame);
        }
    }
}
