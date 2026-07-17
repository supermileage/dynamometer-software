using Dyno.Core.Messages;

namespace Dyno.Core.Protocol;

/// <summary>
/// Names a command opcode.
/// </summary>
/// <remarks>
/// An opcode means nothing on its own: it is namespaced by the task the frame was addressed to, so
/// opcode 1 is <c>USB_CMD_SET_SYSCONFIG</c> to the USB controller and something else entirely to any
/// other task. Naming one therefore takes both halves, which is why the generated
/// <c>Messages.cs</c> — where each task's opcodes are their own enum — cannot do it alone, and why
/// this mapping is hand-written. <c>CommandOpcodeTests</c> fails if a task gains commands and this
/// is not taught about them.
/// </remarks>
public static class CommandOpcodes
{
    /// <summary>The opcode's name, or a plain "opcode N" for a task whose command set we don't
    /// know — a frame from a firmware newer than this host, or a desync that got lucky.</summary>
    public static string Name(task_offset_t task, ushort opcode) =>
        task switch
        {
            task_offset_t.TASK_OFFSET_USB_CONTROLLER
                when Enum.IsDefined((usb_controller_command_t)opcode) => (
                (usb_controller_command_t)opcode
            ).ToString(),
            // The force sensor no longer defines command opcodes (its ADS1115 config is sysconfig),
            // so anything addressed to it falls through to the generic name.
            _ => $"opcode {opcode}",
        };
}
