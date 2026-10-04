using Avalonia.Input;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Behaviors;

// The hex view's keys for the byte editor: hex digits (the top row and the keypad, A–F), the cursor keys, Insert,
// Delete and Backspace; a key with Ctrl, Alt or Meta is left to the window.
internal static class HexKeys
{
    /// <summary>Gives the key to the editor; false when it is not one the editor uses.</summary>
    public static bool Handle(HexEditor editor, Key key, KeyModifiers modifiers)
    {
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
        {
            return false;
        }

        var digit = key switch
        {
            >= Key.D0 and <= Key.D9 when modifiers == KeyModifiers.None => key - Key.D0,
            >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
            >= Key.A and <= Key.F => key - Key.A + 10,
            _ => -1,
        };
        if (digit >= 0)
        {
            editor.TypeDigit(digit);
            return true;
        }

        HexKey? hexKey = key switch
        {
            Key.Left => HexKey.Left,
            Key.Right => HexKey.Right,
            Key.Up => HexKey.Up,
            Key.Down => HexKey.Down,
            Key.PageUp => HexKey.PageUp,
            Key.PageDown => HexKey.PageDown,
            Key.Home => HexKey.Home,
            Key.End => HexKey.End,
            Key.Insert => HexKey.Insert,
            Key.Delete => HexKey.Delete,
            Key.Back => HexKey.Backspace,
            _ => null,
        };
        if (hexKey is not { } known)
        {
            return false;
        }

        editor.OnKey(known);
        return true;
    }
}
