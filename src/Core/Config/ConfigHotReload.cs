using System;
using System.Linq;
using System.Reflection;

namespace TerrariaModder.Core.Config
{
    /// <summary>The optional public, parameterless, void config callback, including inherited implementations.</summary>
    public static class ConfigHotReload
    {
        public static MethodInfo FindCallback(object instance)
        {
            return instance?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "OnConfigChanged" &&
                    method.ReturnType == typeof(void) && !method.ContainsGenericParameters &&
                    method.GetParameters().Length == 0);
        }
    }
}
