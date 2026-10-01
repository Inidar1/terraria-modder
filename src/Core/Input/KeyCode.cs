namespace TerrariaModder.Core.Input
{
    /// <summary>
    /// Common key codes matching XNA Keys enum values.
    /// Use these to avoid XNA assembly dependency.
    /// </summary>
    public static class KeyCode
    {
        // Special
        public const int None = 0;

        // Letters A-Z (65-90)
        public const int A = 65;
        public const int B = 66;
        public const int C = 67;
        public const int D = 68;
        public const int E = 69;
        public const int F = 70;
        public const int G = 71;
        public const int H = 72;
        public const int I = 73;
        public const int J = 74;
        public const int K = 75;
        public const int L = 76;
        public const int M = 77;
        public const int N = 78;
        public const int O = 79;
        public const int P = 80;
        public const int Q = 81;
        public const int R = 82;
        public const int S = 83;
        public const int T = 84;
        public const int U = 85;
        public const int V = 86;
        public const int W = 87;
        public const int X = 88;
        public const int Y = 89;
        public const int Z = 90;

        // Numbers 0-9 (48-57)
        public const int D0 = 48;
        public const int D1 = 49;
        public const int D2 = 50;
        public const int D3 = 51;
        public const int D4 = 52;
        public const int D5 = 53;
        public const int D6 = 54;
        public const int D7 = 55;
        public const int D8 = 56;
        public const int D9 = 57;

        // Function keys F1-F12 (112-123)
        public const int F1 = 112;
        public const int F2 = 113;
        public const int F3 = 114;
        public const int F4 = 115;
        public const int F5 = 116;
        public const int F6 = 117;
        public const int F7 = 118;
        public const int F8 = 119;
        public const int F9 = 120;
        public const int F10 = 121;
        public const int F11 = 122;
        public const int F12 = 123;

        // Modifiers
        public const int LeftShift = 160;
        public const int RightShift = 161;
        public const int LeftControl = 162;
        public const int RightControl = 163;
        public const int LeftAlt = 164;
        public const int RightAlt = 165;

        // Common keys
        public const int Space = 32;
        public const int Enter = 13;
        public const int Escape = 27;
        public const int Tab = 9;
        public const int Back = 8;
        public const int Delete = 46;
        public const int Insert = 45;
        public const int Home = 36;
        public const int End = 35;
        public const int PageUp = 33;
        public const int PageDown = 34;

        // Arrow keys
        public const int Up = 38;
        public const int Down = 40;
        public const int Left = 37;
        public const int Right = 39;

        // Numpad
        public const int NumPad0 = 96;
        public const int NumPad1 = 97;
        public const int NumPad2 = 98;
        public const int NumPad3 = 99;
        public const int NumPad4 = 100;
        public const int NumPad5 = 101;
        public const int NumPad6 = 102;
        public const int NumPad7 = 103;
        public const int NumPad8 = 104;
        public const int NumPad9 = 105;
        public const int Multiply = 106;
        public const int Add = 107;
        public const int Subtract = 109;
        public const int Decimal = 110;
        public const int Divide = 111;
        public const int NumPadEnter = 13; // Same as Enter for most systems

        // Symbols
        public const int OemTilde = 192;      // `~
        public const int OemMinus = 189;      // -_
        public const int OemPlus = 187;       // =+
        public const int OemOpenBrackets = 219;  // [{
        public const int OemCloseBrackets = 221; // ]}
        public const int OemPipe = 220;       // \|
        public const int OemSemicolon = 186;  // ;:
        public const int OemQuotes = 222;     // '"
        public const int OemComma = 188;      // ,<
        public const int OemPeriod = 190;     // .>
        public const int OemQuestion = 191;   // /?

        // Mouse buttons (special values above 255)
        public const int MouseLeft = 1000;
        public const int MouseRight = 1001;
        public const int MouseMiddle = 1002;
        public const int MouseSide1 = 1003;
        public const int MouseSide2 = 1004;

        /// <summary>
        /// Get key name from code.
        /// </summary>
        public static string GetName(int keyCode)
        {
            if (keyCode >= 65 && keyCode <= 90)
                return ((char)keyCode).ToString();
            if (keyCode >= 48 && keyCode <= 57)
                return (keyCode - 48).ToString();
            if (keyCode >= 112 && keyCode <= 123)
                return "F" + (keyCode - 111);
            if (keyCode >= 96 && keyCode <= 105)
                return "Num" + (keyCode - 96);

            switch (keyCode)
            {
                case Space: return "Space";
                case Enter: return "Enter";
                case Escape: return "Escape";
                case Tab: return "Tab";
                case Back: return "Backspace";
                case Delete: return "Delete";
                case Insert: return "Insert";
                case Home: return "Home";
                case End: return "End";
                case PageUp: return "PageUp";
                case PageDown: return "PageDown";
                case Up: return "Up";
                case Down: return "Down";
                case Left: return "Left";
                case Right: return "Right";
                case LeftShift: return "LShift";
                case RightShift: return "RShift";
                case LeftControl: return "LCtrl";
                case RightControl: return "RCtrl";
                case LeftAlt: return "LAlt";
                case RightAlt: return "RAlt";
                case OemTilde: return "Tilde";
                case OemMinus: return "Minus";
                case OemPlus: return "Plus";
                case OemPipe: return "Backslash";
                case OemOpenBrackets: return "[";
                case OemCloseBrackets: return "]";
                case OemSemicolon: return ";";
                case OemQuotes: return "'";
                case OemComma: return ",";
                case OemPeriod: return ".";
                case OemQuestion: return "/";
                case Multiply: return "NumMul";
                case Add: return "NumAdd";
                case Subtract: return "NumSub";
                case Decimal: return "NumDec";
                case Divide: return "NumDiv";
                case MouseLeft: return "MouseLeft";
                case MouseRight: return "MouseRight";
                case MouseMiddle: return "MouseMiddle";
                case MouseSide1: return "MouseSide1";
                case MouseSide2: return "MouseSide2";
                default: return $"Key{keyCode}";
            }
        }

        /// <summary>
        /// Parse key name to code.
        /// </summary>
        public static int Parse(string name)
        {
            if (string.IsNullOrEmpty(name)) return None;

            name = name.Trim().ToUpperInvariant();

            // Single letter
            if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z')
                return name[0];

            // Single digit
            if (name.Length == 1 && name[0] >= '0' && name[0] <= '9')
                return name[0];

            // Function keys
            if (name.StartsWith("F") && int.TryParse(name.Substring(1), out int fNum) && fNum >= 1 && fNum <= 12)
                return 111 + fNum;

            // NumPad keys
            if (name.StartsWith("NUMPAD") && int.TryParse(name.Substring(6), out int numPadNum) && numPadNum >= 0 && numPadNum <= 9)
                return 96 + numPadNum;
            if (name.StartsWith("NUM") && int.TryParse(name.Substring(3), out int numNum) && numNum >= 0 && numNum <= 9)
                return 96 + numNum;

            switch (name)
            {
                case "SPACE": return Space;
                case "ENTER": case "RETURN": return Enter;
                case "ESCAPE": case "ESC": return Escape;
                case "TAB": return Tab;
                case "BACKSPACE": case "BACK": return Back;
                case "DELETE": case "DEL": return Delete;
                case "INSERT": case "INS": return Insert;
                case "HOME": return Home;
                case "END": return End;
                case "PAGEUP": case "PGUP": return PageUp;
                case "PAGEDOWN": case "PGDN": return PageDown;
                case "UP": return Up;
                case "DOWN": return Down;
                case "LEFT": return Left;
                case "RIGHT": return Right;
                case "TILDE": case "~": case "`": case "OEMTILDE": return OemTilde;
                case "MINUS": case "-": return OemMinus;
                case "PLUS": case "=": return OemPlus;
                case "MOUSELEFT": case "LMB": return MouseLeft;
                case "MOUSERIGHT": case "RMB": return MouseRight;
                case "MOUSEMIDDLE": case "MMB": return MouseMiddle;
                case "MOUSESIDE1": return MouseSide1;
                case "MOUSESIDE2": return MouseSide2;
                case "NUMMUL": return Multiply;
                case "NUMADD": return Add;
                case "NUMSUB": return Subtract;
                case "NUMDEC": return Decimal;
                case "NUMDIV": return Divide;
                case "LSHIFT": case "LEFTSHIFT": return LeftShift;
                case "RSHIFT": case "RIGHTSHIFT": return RightShift;
                case "LCTRL": case "LEFTCONTROL": case "LCONTROL": return LeftControl;
                case "RCTRL": case "RIGHTCONTROL": case "RCONTROL": return RightControl;
                case "LALT": case "LEFTALT": return LeftAlt;
                case "RALT": case "RIGHTALT": return RightAlt;
                case "BACKSLASH": case "\\": case "OEMPIPE": case "OEMBACKSLASH": return OemPipe;
                case "[": case "OPENBRACKET": case "OEM4": return OemOpenBrackets;
                case "]": case "CLOSEBRACKET": case "OEM6": return OemCloseBrackets;
                case ";": case "SEMICOLON": case "OEM1": return OemSemicolon;
                case "'": case "QUOTE": case "OEM7": return OemQuotes;
                case ",": case "COMMA": case "OEMCOMMA": return OemComma;
                case ".": case "PERIOD": case "OEMPERIOD": return OemPeriod;
                case "/": case "SLASH": case "QUESTION": case "OEMQUESTION": return OemQuestion;
                default: return None;
            }
        }
    }
}
