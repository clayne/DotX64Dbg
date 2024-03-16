using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Dotx64Dbg
{
    internal partial class Plugins
    {
        internal bool IsSystemType(Type t)
        {
            return false;
        }

        Type GetPluginClass(Assembly assembly)
        {
            var entries = assembly.GetTypes().Where(a => a.GetInterface(nameof(IPlugin)) != null).ToArray();
            if (entries.Length > 1)
            {
                throw new Exception("Assembly has multiple classes with IPlugin, can have only one entry.");
            }
            if (entries.Length == 0)
            {
                throw new Exception("Assembly has no IPlugin class.");
            }
            return entries.First();
        }

        void UnloadPluginInstance(Plugin plugin, CancellationToken token, bool isReloading = false)
        {
            token.ThrowIfCancellationRequested();

            if (plugin.Instance == null)
                return;

            var pluginName = plugin.Info != null ? plugin.Info.Name : plugin.Path;
            Utils.DebugPrintLine($"Unloading plugin: {plugin.Path}");

            if (!isReloading)
            {
                plugin.Instance.Shutdown();
            }

            Commands.RemoveAllFor(plugin);
            Expressions.RemoveAllFor(plugin);
            Menus.RemoveAllFor(plugin);

            Console.WriteLine($"[DotX64Dbg] Unloaded plugin: {pluginName}");
        }

        void RegisterPluginCommand(Plugin plugin, MethodInfo fn, Command cmd, object obj)
        {
            Commands.Handler cb = null;

            var hasArgs = fn.GetParameters().Length > 0;

            if (fn.ReturnType == typeof(void) && hasArgs)
            {
                var cb2 = fn.CreateDelegate<Commands.HandlerVoid>(obj);
                cb = (string[] args) =>
                {
                    cb2(args);
                    return true;
                };
            }
            if (fn.ReturnType == typeof(void) && !hasArgs)
            {
                var cb2 = fn.CreateDelegate<Commands.HandlerVoidNoArgs>(obj);
                cb = (string[] args) =>
                {
                    cb2();
                    return true;
                };
            }
            else if (fn.ReturnType == typeof(bool) && !hasArgs)
            {
                var cb2 = fn.CreateDelegate<Commands.HandlerNoArgs>(obj);
                cb = (string[] args) =>
                {
                    return cb2();
                };
            }
            else if (fn.ReturnType == typeof(bool) && hasArgs)
            {
                cb = fn.CreateDelegate<Commands.Handler>(obj);
            }

            Commands.Register(plugin, cmd.Name, cmd.DebugOnly, cb);
        }

        void RegisterExpression(Plugin plugin, MethodInfo fn, Expression expr, object obj)
        {
            if (fn.ReturnType != typeof(nuint))
            {
                throw new Exception($"Expression functions must return 'nuint', Function: {fn.Name}");
            }

            var args = fn.GetParameters();
            foreach (var arg in args)
            {
                if (arg.ParameterType != typeof(nuint))
                {
                    throw new Exception($"Expression arguments must be 'nuint', Function: {fn.Name}");
                }
            }

            if (args.Length == 0)
            {
                var cb = fn.CreateDelegate<Expressions.ExpressionFunc0>(obj);
                Expressions.Register(plugin, expr.Name, cb);
            }
            else if (args.Length == 1)
            {
                var cb = fn.CreateDelegate<Expressions.ExpressionFunc1>(obj);
                Expressions.Register(plugin, expr.Name, cb);
            }
            else if (args.Length == 2)
            {
                var cb = fn.CreateDelegate<Expressions.ExpressionFunc2>(obj);
                Expressions.Register(plugin, expr.Name, cb);
            }
            else if (args.Length == 3)
            {
                var cb = fn.CreateDelegate<Expressions.ExpressionFunc3>(obj);
                Expressions.Register(plugin, expr.Name, cb);
            }
        }

        void RegisterMenu(Plugin plugin, MethodInfo fn, UI.Menu menu, object obj)
        {
            var cb = fn.CreateDelegate<UI.Menu.MenuDelegate>(obj);

            var menuPath = menu.Path;
            var rootMenu = menu.Parent.ToString();

            Menus.AddMenu(plugin, $"{rootMenu}/{plugin.Info.Name}/{menuPath}", cb);
        }

        void LoadPluginInstanceRecursive(Plugin plugin, object obj, HashSet<object> processed, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (obj == null)
                return;

            if (obj.GetType().Assembly != plugin.Loader.Current)
                return;

            processed.Add(obj);

            var instType = obj.GetType();
            var funcs = instType.GetRuntimeMethods();

            foreach (var fn in funcs)
            {
                var attribs = fn.GetCustomAttributes();
                foreach (var attrib in attribs)
                {
                    if (attrib is Command cmd)
                    {
                        RegisterPluginCommand(plugin, fn, cmd, obj);
                    }
                    else if (attrib is Expression expr)
                    {
                        RegisterExpression(plugin, fn, expr, obj);
                    }
                    else if (attrib is UI.Menu menu)
                    {
                        RegisterMenu(plugin, fn, menu, obj);
                    }
                }
            }

            var fields = instType.GetRuntimeFields();
            foreach (var field in fields)
            {
                var fieldType = field.FieldType;
                if (IsSystemType(fieldType))
                    continue;

                if (fieldType.IsClass && !fieldType.IsArray)
                {
                    var nextObj = field.GetValue(obj);
                    if (!processed.Contains(nextObj))
                    {
                        LoadPluginInstanceRecursive(plugin, nextObj, processed, token);
                    }
                }
            }

            var props = instType.GetRuntimeProperties();
            foreach (var prop in props)
            {
                var fieldType = prop.PropertyType;
                if (IsSystemType(fieldType))
                    continue;
                if (prop.GetIndexParameters().Count() > 0)
                    continue;

                if (fieldType.IsClass && !fieldType.IsArray)
                {
                    var nextObj = prop.GetValue(obj);
                    if (!processed.Contains(nextObj))
                    {
                        LoadPluginInstanceRecursive(plugin, nextObj, processed, token);
                    }
                }
            }

        }

        void LoadPluginInstance(Plugin plugin, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (plugin.Instance == null)
                return;

            LoadPluginInstanceRecursive(plugin, plugin.Instance, new(), token);
        }

        bool ReloadPlugin(Plugin plugin, string newAssemblyPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var hotReload = Settings.EnableHotloading && plugin.Instance != null;

            Console.WriteLine($"[DotX64Dbg] {(hotReload ? "Reloading" : "Loading")} '{plugin.Info.Name}'");

            try
            {
                UnloadPluginInstance(plugin, token, hotReload);

                var loader = new AssemblyLoader();
                loader.AddExternalRequiredAssemblies(plugin.ResolveDependencies(dependencyResolver, token));
                var newAssembly = loader.LoadFromFile(newAssemblyPath);

                var pluginClass = GetPluginClass(newAssembly);
                if (pluginClass != null)
                {
                    Utils.DebugPrintLine("Entry class: {0}", pluginClass.Name);
                }

                // NOTE: RemapContext stores old references, to fully unload the dll
                // it must be disposed first.
                using (var ctx = new Hotload.Context(plugin.Loader?.Current, newAssembly))
                {
                    var newInstance = ctx.Create(pluginClass);

                    if (hotReload)
                    {
                        Hotload.AdaptStatics(ctx, plugin.Loader.Current, newAssembly);
                        Hotload.AdaptClass(ctx, plugin.Instance, plugin.InstanceType, newInstance, pluginClass);
                    }
                    else
                    {
                        // Initial startup.
                        var ctor = pluginClass.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Array.Empty<Type>(), null);
                        if (ctor != null)
                        {
                            ctor.Invoke(newInstance, Array.Empty<object>());
                        }

                        var startup = pluginClass.GetMethod("Startup");
                        if (startup != null)
                        {
                            try
                            {
                                startup.Invoke(newInstance, Array.Empty<object>());
                            }
                            catch (Exception ex)
                            {
                                Utils.PrintException(ex);
                            }
                        }
                    }

                    plugin.Instance = newInstance as IPlugin;
                    plugin.InstanceType = pluginClass;

                    if (hotReload)
                    {
                        var reloadables = ctx.GetObjectsWithInterface(typeof(IHotload));
                        foreach (var obj in reloadables)
                        {
                            var reloadable = obj as IHotload;
                            reloadable.OnHotload();
                        }
                    }
                }

                if (plugin.Loader != null)
                {
                    var cur = plugin.Loader;
                    plugin.Loader = null;

                    var oldAssemblyPath = plugin.AssemblyPath;
                    var oldPdbPath = oldAssemblyPath.Replace(".dll", ".pdb");

                    cur.UnloadCurrent();
                    cur = null;

                    for (int i = 0; i < 50; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }

                    token.ThrowIfCancellationRequested();

                    Task.Run(async delegate
                    {
                        await Task.Delay(2000);

                        // Remove previous assembly.
                        try
                        {
                            System.IO.File.Delete(oldAssemblyPath);
                        }
                        catch (Exception)
                        {
                            Utils.DebugPrintLine("WARNING: Unable to remove old assembly, ensure no references are stored.");
                        }

                        // Remove previous debug symbols.
                        // NOTE: If the debugger is attached this may be locked.
                        try
                        {
                            System.IO.File.Delete(oldPdbPath);
                        }
                        catch (Exception)
                        {
                            Utils.DebugPrintLine("WARNING: Unable to remove old PDB file, will be removed next start, probably locked by debugger.");
                        }
                    });
                }

                plugin.Loader = loader;
                plugin.AssemblyPath = newAssemblyPath;

                LoadPluginInstance(plugin, token);

                Menus.AddPluginMenu(plugin);
            }
            catch (Exception ex)
            {
                Utils.PrintException(ex);
                return false;
            }

            Console.WriteLine($"[DotX64Dbg] {(hotReload ? "Reloaded" : "Loaded")} '{plugin.Info.Name}'");
            return true;
        }

        public void UnloadPlugin(Plugin plugin, CancellationToken token = default(CancellationToken))
        {
            if (plugin.Instance != null)
            {
                UnloadPluginInstance(plugin, token);
                plugin.Instance = null;
            }

            if (plugin.Loader != null)
            {
                plugin.Loader.UnloadCurrent();
                plugin.Loader = null;
            }

            plugin.RequiresRebuild = false;
        }

    }
}
