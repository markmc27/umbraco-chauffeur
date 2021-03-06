﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chauffeur.DependencyBuilders;
using Umbraco.Core;
using Umbraco.Core.Logging;

namespace Chauffeur.Host
{
    public sealed class UmbracoHost : IChauffeurHost, IDisposable
    {
        private readonly TextReader reader;
        private readonly TextWriter writer;

        public static UmbracoHost Current { get; set; }
        internal ShittyIoC Container { get; private set; }

        public UmbracoHost(TextReader reader, TextWriter writer)
        {
            this.reader = reader;
            this.writer = writer;

            Container = new ShittyIoC();
            Container.Register(() => reader);
            Container.Register(() => writer);
            Container.Register<IChauffeurHost>(() => this);

            Container.RegisterFrom<BootManagerBuilder>();
            Container.RegisterFrom<ChauffeurSettingBuilder>();
            Container.RegisterFrom<FileSystemBuilder>();
            Container.RegisterFrom< SqlSyntaxProviderBuilder>();

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var builders = assemblies
                .Where(asm => !asm.IsDynamic)
#if DEBUG
                .Where(asm => !asm.FullName.Contains("xunit"))
#endif
                .SelectMany(a => a.GetExportedTypes())
                .Where(t => t.IsClass)
                .Where(t => typeof(IBuildDependencies).IsAssignableFrom(t))
                .ToArray();

            foreach (var builder in builders)
                Container.RegisterFrom(builder);

            FreezeResolution();
        }

        private static void FreezeResolution()
        {
            var resolutionType = typeof(CoreBootManager).Assembly.GetTypes().FirstOrDefault(t => t.Name == "Resolution");

            if (resolutionType != null)
            {
                var freezeMethod = resolutionType.GetMethod("Freeze", BindingFlags.Public | BindingFlags.Static);
                freezeMethod.Invoke(null, null);
            }
        }

        public async Task<DeliverableResponse> Run()
        {
            await writer.WriteLineAsync("Welcome to Chauffeur, your Umbraco console.");
            var fvi = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location);
            await writer.WriteLineFormattedAsync("You're running Chauffeur v{0} against Umbraco '{1}'", fvi.FileVersion, ConfigurationManager.AppSettings["umbracoConfigurationStatus"]);
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("Type `help` to list the commands and `help <command>` for help for a specific command.");
            await writer.WriteLineAsync();

            var result = DeliverableResponse.Continue;

            while (result != DeliverableResponse.Shutdown)
            {
                var command = await Prompt();

                result = await Process(command);
            }

            return result;
        }

        public async Task<DeliverableResponse> Run(string[] args)
        {
            return await Process(string.Join(" ", args));
        }

        private async Task<DeliverableResponse> Process(string command)
        {
            if (string.IsNullOrEmpty(command))
                return DeliverableResponse.Continue;

            var args = command.Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries);

            var what = args[0].ToLower();
            args = args.Skip(1).ToArray();

            try
            {
                var deliverable = Container.ResolveDeliverableByName(what);
                return await deliverable.Run(what, args);
            }
            catch (Exception ex)
            {
                writer.WriteLine("Error running the current deliverable '" + what + "': " + ex.Message);
                LogHelper.Error<UmbracoHost>("Error running the current deliverable", ex);

                if (ex is TypeLoadException)
                {
                    var tlex = (TypeLoadException)ex;

                    if (tlex.TypeName == "Umbraco.Web.Security.Providers.MembersMembershipProvider")
                    {
                        writer.WriteLine("The problem is likely caused by what was identified in U4-4781. Don't worry though it's an easy fix. You need to change your web.config and explicitly specify the assembly name of the Membership & User providers.");
                        writer.WriteLine("Make sure the type looks like:");
                        writer.WriteLine("\tUmbraco.Web.Security.Providers.UsersMembershipProvider, Umbraco");
                        writer.WriteLine("So Chauffeur will know it's coming from Umbraco.dll and not System.Web.dll");
                    }
                }

                return DeliverableResponse.FinishedWithError;
            }
        }

        private async Task<string> Prompt()
        {
            await writer.WriteAsync("umbraco> ");
            return await reader.ReadLineAsync();
        }

        public void Dispose()
        {
            ((IDisposable)ApplicationContext.Current).Dispose();
        }
    }
}
