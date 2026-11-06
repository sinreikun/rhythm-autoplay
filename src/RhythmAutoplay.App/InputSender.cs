using System.Runtime.InteropServices;

namespace RhythmAutoplay.App;

/// <summary>
/// SendInput によるキー送信。ゲーム可読性を優先してスキャンコード方式で送る。
/// HYPNOSYNC はレビューに「外部ツール対策で入力を取りこぼすことがある」との
/// 報告があり、通るかどうかは v0.2 実機検証の最初の確認点。
/// </summary>
public static class InputSender
{
    public static ushort KeyNameToVirtualKey(string key)
    {
        string k = key.Trim().ToUpperInvariant();
        if (k.Length == 1)
        {
            char c = k[0];
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                return c; // 英数字は文字コード = 仮想キーコード
            }
        }
        return k switch
        {
            "ESC" or "ESCAPE" => 0x1B,
            "ENTER" or "RETURN" => 0x0D,
            "SPACE" => 0x20,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            _ => throw new ArgumentException($"未知のキー名です: {key}"),
        };
    }

    public static void Send(string key, bool isDown)
    {
        ushort vk = KeyNameToVirtualKey(key);
        ushort scan = (ushort)MapVirtualKey(vk, 0); // MAPVK_VK_TO_VSC
        var input = new INPUT
        {
            type = 1, // INPUT_KEYBOARD
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = (uint)(0x0008 | (isDown ? 0 : 0x0002)), // SCANCODE | KEYUP
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero,
                },
            },
        };
        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (sent != 1)
        {
            throw new InvalidOperationException($"SendInput に失敗しました (key={key}, down={isDown})。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
