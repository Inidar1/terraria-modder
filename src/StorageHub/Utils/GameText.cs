using System;
using System.Reflection;

namespace StorageHub.Utils
{
    internal static class GameText
    {
        private static bool _reflectionInitialized;
        private static MethodInfo _newTextMethod;

        public static void Show(string text, byte r = 255, byte g = 240, byte b = 20)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                if (!_reflectionInitialized)
                    InitializeReflection();

                if (_newTextMethod != null)
                {
                    var args = _newTextMethod.GetParameters().Length >= 4
                        ? new object[] { text, r, g, b }
                        : new object[] { text };
                    _newTextMethod.Invoke(null, args);
                }
            }
            catch
            {
                // Best-effort UI message helper.
            }
        }

        private static void InitializeReflection()
        {
            _reflectionInitialized = true;

            try
            {
                var mainType = Type.GetType("Terraria.Main, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Main");
                if (mainType == null) return;

                foreach (var method in mainType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(method.Name, "NewText", StringComparison.Ordinal))
                        continue;

                    var parameters = method.GetParameters();
                    if (parameters.Length >= 1 && parameters[0].ParameterType == typeof(string))
                    {
                        _newTextMethod = method;
                        if (parameters.Length >= 4)
                            break;
                    }
                }
            }
            catch
            {
                // Intentionally ignored.
            }
        }
    }
}

