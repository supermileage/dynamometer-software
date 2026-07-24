using Dyno.Core.Messages;

namespace Dyno.Core.Protocol;

/// <summary>A task error/warning unpacked from the 32-bit packed <c>error_code</c>.</summary>
/// <param name="Task">Producing task (the high 16 bits).</param>
/// <param name="Number">Task-local error number (low 15 bits).</param>
/// <param name="IsWarning">True when the warning flag (bit 15) is set.</param>
/// <param name="Raw">The original packed code.</param>
public readonly record struct DecodedError(
    task_offset_t Task,
    uint Number,
    bool IsWarning,
    uint Raw
);

/// <summary>Decodes the packed <c>error_code</c> per the scheme documented in the schema.</summary>
/// <remarks>Only the unpacking lives here. What a code <em>means</em> — its name and the sentence
/// shown for it — comes from <see cref="ErrorCatalog"/>, generated from the same schema and keyed
/// on <see cref="DecodedError.Raw"/>: the number on its own is task-local and says nothing, so the
/// packed code is the only sound lookup key.</remarks>
public static class ErrorDecoder
{
    public static DecodedError Decode(uint errorCode) =>
        new(
            Task: (task_offset_t)(errorCode & MessageConstants.TASK_OFFSET_MASK),
            Number: errorCode & MessageConstants.TASK_ERROR_NUM_MASK,
            IsWarning: (errorCode & MessageConstants.WARNING_FLAG) != 0,
            Raw: errorCode
        );
}
