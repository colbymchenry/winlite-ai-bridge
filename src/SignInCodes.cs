// A tool's own device sign-in prints a one-time code and the page to enter it on. The bridge gives
// the caller the shapes of those (tools/list: deviceCode, devicePage) so the caller can copy the
// code from the tool's screen as it scrolls by. These helpers find the code in the tool's plain text;
// the bridge's own self-test uses them to check each tool's recorded sign-in.
using System;
using System.Text.RegularExpressions;

namespace WinLiteBridge
{
    internal static class SignInCodes
    {
        // The tool's text without its colours and cursor moves.
        public static string Plain(string text)
        {
            return Regex.Replace(text ?? "", @"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)", "");
        }

        // The device code in the tool's plain text, or null.
        public static string CodeIn(string plain, Regex pattern)
        {
            if (pattern == null) { return null; }
            Match code = pattern.Match(plain ?? "");
            if (!code.Success) { return null; }
            return code.Groups.Count > 1 && code.Groups[1].Success ? code.Groups[1].Value : code.Value;
        }
    }
}
