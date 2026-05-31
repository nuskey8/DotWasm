using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DotWasm.Models;

namespace DotWasm.Runtime;

internal sealed partial class WasmExecutionContext
{
    readonly struct WasmLoad<TMemoryIndex, TOffset, TStorage, TValue, TNext> : IWasmCompiledOp
        where TMemoryIndex : ILiteral<int>
        where TOffset : ILiteral<int>
        where TStorage : unmanaged, INumberBase<TStorage>
        where TValue : INumberBase<TValue>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var memory = frame.Instance.GetMemoryInstance(TMemoryIndex.Value);
            var address = context.LoadMemoryAddress(memory, (uint)TOffset.Value, Unsafe.SizeOf<TStorage>());
            var stored = Unsafe.ReadUnaligned<TStorage>(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(memory.Data), address)
            );
            Push(context, TValue.CreateTruncating(stored));
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmStore<TMemoryIndex, TOffset, TValue, TStorage, TNext> : IWasmCompiledOp
        where TMemoryIndex : ILiteral<int>
        where TOffset : ILiteral<int>
        where TValue : INumberBase<TValue>
        where TStorage : unmanaged, INumberBase<TStorage>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var memory = frame.Instance.GetMemoryInstance(TMemoryIndex.Value);
            var value = TStorage.CreateTruncating(Pop<TValue>(context));
            var address = context.LoadMemoryAddress(memory, (uint)TOffset.Value, Unsafe.SizeOf<TStorage>());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(memory.Data), address), value);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmMemorySize<TMemoryIndex, TNext> : IWasmCompiledOp
        where TMemoryIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var memory = frame.Instance.GetMemoryInstance(TMemoryIndex.Value);
            var pages = memory.Data.Length / WasmStore.PageSize;
            if (memory.AddressType == AddressType.I64)
                context.valueStack.Push(WasmValue.FromI64(pages));
            else
                context.valueStack.Push(WasmValue.FromI32(pages));
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmMemoryGrow<TMemoryIndex, TNext> : IWasmCompiledOp
        where TMemoryIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var memory = frame.Instance.GetMemoryInstance(TMemoryIndex.Value);
            var deltaPages = context.PopPageCount(memory);
            var oldPages = memory.Data.Length / WasmStore.PageSize;
            var maxPages = memory.Max ?? 65536u;
            if (deltaPages < 0 || (ulong)(uint)oldPages + (uint)deltaPages > maxPages)
            {
                PushFailure(context, memory);
                return TNext.Run(context, ref frame);
            }

            try
            {
                memory.Grow(deltaPages);
                if (memory.AddressType == AddressType.I64)
                    context.valueStack.Push(WasmValue.FromI64(oldPages));
                else
                    context.valueStack.Push(WasmValue.FromI32(oldPages));
            }
            catch (Exception ex)
                when (ex is InvalidOperationException or OverflowException or OutOfMemoryException)
            {
                PushFailure(context, memory);
            }

            return TNext.Run(context, ref frame);
        }

        static void PushFailure(WasmExecutionContext context, MemoryInstance memory)
        {
            if (memory.AddressType == AddressType.I64)
                context.valueStack.Push(WasmValue.FromI64(-1));
            else
                context.valueStack.Push(WasmValue.FromI32(-1));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int LoadMemoryAddress(MemoryInstance memory, uint offset, int width) =>
        CalcMemoryAddress(PopMemoryAddress(memory), offset, width, memory.Data.Length);
}
