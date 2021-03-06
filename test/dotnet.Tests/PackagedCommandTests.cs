﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.TestFramework;
using Microsoft.DotNet.Tools.Test.Utilities;
using Microsoft.DotNet.InternalAbstractions;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Build.Construction;
using System.Linq;
using Microsoft.Build.Evaluation;
using System.Xml.Linq;

namespace Microsoft.DotNet.Tests
{
    public class PackagedCommandTests : TestBase
    {
        private readonly ITestOutputHelper _output;

        public PackagedCommandTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData("AppWithDirectAndToolDep")]
        [InlineData("AppWithToolDependency")]
        public void TestProjectToolIsAvailableThroughDriver(string appName)
        {
            var testInstance = TestAssets.Get(appName)
                .CreateInstance()
                .WithSourceFiles()
                .WithNuGetConfig(RepoDirectoriesProvider.TestPackages);

            // restore again now that the project has changed
            new RestoreCommand()
                .WithWorkingDirectory(testInstance.Root)
                .Execute()
                .Should().Pass();

            new BuildCommand()
                .WithProjectDirectory(testInstance.Root)
                .Execute()
                .Should().Pass();

            new PortableCommand()
                .WithWorkingDirectory(testInstance.Root)
                .ExecuteWithCapturedOutput()
                .Should().HaveStdOutContaining("Hello Portable World!")
                     .And.NotHaveStdErr()
                     .And.Pass();
        }

        [RequiresSpecificFrameworkTheory("netcoreapp1.1")]
        [InlineData(true)]
        [InlineData(false)]
        public void IfPreviousVersionOfSharedFrameworkIsInstalled_ToolsTargetingItRun(bool toolPrefersCLIRuntime)
        {
            var testInstance = TestAssets.Get("AppWithToolDependency")
                .CreateInstance(identifier: toolPrefersCLIRuntime ? "preferCLIRuntime" : "")
                .WithSourceFiles()
                .WithNuGetConfig(RepoDirectoriesProvider.TestPackages);

            testInstance = testInstance.WithProjectChanges(project =>
            {
                var ns = project.Root.Name.Namespace;

                var toolReference = project.Descendants(ns + "DotNetCliToolReference")
                    .Where(tr => tr.Attribute("Include").Value == "dotnet-portable")
                    .Single();

                toolReference.Attribute("Include").Value =
                    toolPrefersCLIRuntime ? "dotnet-portable-v1-prefercli" : "dotnet-portable-v1";
            });

            testInstance = testInstance.WithRestoreFiles();

            new BuildCommand()
                .WithProjectDirectory(testInstance.Root)
                .Execute()
                .Should().Pass();

            var result = new DotnetCommand()
                .WithWorkingDirectory(testInstance.Root)
                .Execute(toolPrefersCLIRuntime ? "portable-v1-prefercli" : "portable-v1");

            result.Should().Pass()
                .And.HaveStdOutContaining("I'm running on shared framework version 1.1.2!");

        }

        [RequiresSpecificFrameworkFact("netcoreapp1.1")]
        public void IfAToolHasNotBeenRestoredForNetCoreApp2_0ItFallsBackToNetCoreApp1_x()
        {
            string toolName = "dotnet-portable-v1";

            var testInstance = TestAssets.Get("AppWithToolDependency")
                .CreateInstance()
                .WithSourceFiles()
                .WithNuGetConfig(RepoDirectoriesProvider.TestPackages);

            string projectFolder = null;

            testInstance = testInstance.WithProjectChanges((projectPath, project) =>
            {
                projectFolder = Path.GetDirectoryName(projectPath);
                var ns = project.Root.Name.Namespace;

                //  Remove reference to tool that won't restore on 1.x
                project.Descendants(ns + "DotNetCliToolReference")
                    .Where(tr => tr.Attribute("Include").Value == "dotnet-PreferCliRuntime")
                    .Remove();

                var toolReference = project.Descendants(ns + "DotNetCliToolReference")
                    .Where(tr => tr.Attribute("Include").Value == "dotnet-portable")
                    .Single();

                toolReference.Attribute("Include").Value = toolName;

                //  Restore tools for .NET Core 1.1
                project.Root.Element(ns + "PropertyGroup")
                    .Add(new XElement(ns + "DotnetCliToolTargetFramework", "netcoreapp1.1"));

                //  Use project-specific global packages folder
                project.Root.Element(ns + "PropertyGroup")
                    .Add(new XElement(ns + "RestorePackagesPath", @"$(MSBuildProjectDirectory)\packages"));

            });

            var toolFolder = Path.Combine(projectFolder,
                                          "packages",
                                          ".tools",
                                          toolName);


            testInstance = testInstance.WithRestoreFiles();

            var result = new DotnetCommand()
                    .WithWorkingDirectory(testInstance.Root)
                    .Execute("portable-v1");

            result.Should().Pass()
                .And.HaveStdOutContaining("I'm running on shared framework version 1.1.2!");
        }

        [Fact]
        public void CanInvokeToolWhosePackageNameIsDifferentFromDllName()
        {
            var testInstance = TestAssets.Get("AppWithDepOnToolWithOutputName")
                .CreateInstance()
                .WithSourceFiles()
                .WithRestoreFiles();

            new BuildCommand()
                .WithProjectDirectory(testInstance.Root)
                .Execute()
                .Should().Pass();

            new GenericCommand("tool-with-output-name")
                .WithWorkingDirectory(testInstance.Root)
                .ExecuteWithCapturedOutput()
                .Should().HaveStdOutContaining("Tool with output name!")
                     .And.NotHaveStdErr()
                     .And.Pass();
        }

        [Fact]
        public void ItShowsErrorWhenToolIsNotRestored()
        {
            var testInstance = TestAssets.Get("NonRestoredTestProjects", "AppWithNonExistingToolDependency")
                .CreateInstance()
                .WithSourceFiles();

            new TestCommand("dotnet")
                .WithWorkingDirectory(testInstance.Root)
                .ExecuteWithCapturedOutput("nonexistingtool")
                .Should().Fail()
                    .And.HaveStdErrContaining(string.Format(LocalizableStrings.NoExecutableFoundMatchingCommand, "dotnet-nonexistingtool"));
        }

        [Fact]
        public void ItRunsToolRestoredToSpecificPackageDir()
        {
            var testInstance = TestAssets.Get("NonRestoredTestProjects", "ToolWithRandomPackageName")
                .CreateInstance()
                .WithSourceFiles();

            var appWithDepOnToolDir = testInstance.Root.Sub("AppWithDepOnTool");
            var toolWithRandPkgNameDir = testInstance.Root.Sub("ToolWithRandomPackageName");
            var pkgsDir = testInstance.Root.CreateSubdirectory("pkgs");

            // 3ebdd4f1-a194-470a-b01a-4515672791d1
            //                         ^-- index = 24
            string randomPackageName = Guid.NewGuid().ToString().Substring(24);

            // TODO: This is a workaround for https://github.com/dotnet/cli/issues/5020
            SetGeneratedPackageName(appWithDepOnToolDir.GetFile("AppWithDepOnTool.csproj"),
                                    randomPackageName);

            SetGeneratedPackageName(toolWithRandPkgNameDir.GetFile("ToolWithRandomPackageName.csproj"),
                                    randomPackageName);

            new RestoreCommand()
                .WithWorkingDirectory(toolWithRandPkgNameDir)
                .Execute()
                .Should().Pass();

            new PackCommand()
                .WithWorkingDirectory(toolWithRandPkgNameDir)
                .Execute($"-o \"{pkgsDir.FullName}\" /p:version=1.0.0")
                .Should().Pass();

            new RestoreCommand()
                .WithWorkingDirectory(appWithDepOnToolDir)
                .Execute($"--source \"{pkgsDir.FullName}\"")
                .Should().Pass();

            new TestCommand("dotnet")
                .WithWorkingDirectory(appWithDepOnToolDir)
                .ExecuteWithCapturedOutput("randompackage")
                .Should().Pass()
                .And.HaveStdOutContaining("Hello World from tool!")
                .And.NotHaveStdErr();
        }

        [Fact(Skip="https://github.com/dotnet/cli/issues/9688")]
        public void ToolsCanAccessDependencyContextProperly()
        {
            var testInstance = TestAssets.Get("DependencyContextFromTool")
                .CreateInstance()
                .WithSourceFiles()
                .WithRestoreFiles();

            new DependencyContextTestCommand(RepoDirectoriesProvider.DotnetUnderTest)
                .WithWorkingDirectory(testInstance.Root)
                .Execute("")
                .Should().Pass();
        }

        [Fact]
        public void TestProjectDependencyIsNotAvailableThroughDriver()
        {
            var testInstance = TestAssets.Get("AppWithDirectDep")
                .CreateInstance()
                .WithSourceFiles()
                .WithNuGetConfig(RepoDirectoriesProvider.TestPackages);

            // restore again now that the project has changed
            new RestoreCommand()
                .WithWorkingDirectory(testInstance.Root)
                .Execute()
                .Should().Pass();

            new BuildCommand()
                .WithWorkingDirectory(testInstance.Root)
                .Execute()
                .Should().Pass();

            var currentDirectory = Directory.GetCurrentDirectory();

            CommandResult result = new HelloCommand()
                .WithWorkingDirectory(testInstance.Root)
                .ExecuteWithCapturedOutput();

            result.StdErr.Should().Contain(string.Format(LocalizableStrings.NoExecutableFoundMatchingCommand, "dotnet-hello"));
            
            result.Should().Fail();        
        }

        private void SetGeneratedPackageName(FileInfo project, string packageName)
        {
            const string propertyName = "GeneratedPackageId";
            var p = ProjectRootElement.Open(project.FullName, new ProjectCollection(), true);
            p.AddProperty(propertyName, packageName);
            p.Save();
        }

        class HelloCommand : DotnetCommand
        {
            public HelloCommand()
            {
            }

            public override CommandResult Execute(string args = "")
            {
                args = $"hello {args}";
                return base.Execute(args);
            }

            public override CommandResult ExecuteWithCapturedOutput(string args = "")
            {
                args = $"hello {args}";
                return base.ExecuteWithCapturedOutput(args);
            }
        }

        class PortableCommand : DotnetCommand
        {
            public PortableCommand()
            {
            }

            public override CommandResult Execute(string args = "")
            {
                args = $"portable {args}";
                return base.Execute(args);
            }

            public override CommandResult ExecuteWithCapturedOutput(string args = "")
            {
                args = $"portable {args}";
                return base.ExecuteWithCapturedOutput(args);
            }
        }

        class GenericCommand : DotnetCommand
        {
            private readonly string _commandName;

            public GenericCommand(string commandName)
            {
                _commandName = commandName;
            }

            public override CommandResult Execute(string args = "")
            {
                args = $"{_commandName} {args}";
                return base.Execute(args);
            }

            public override CommandResult ExecuteWithCapturedOutput(string args = "")
            {
                args = $"{_commandName} {args}";
                return base.ExecuteWithCapturedOutput(args);
            }
        }

        class DependencyContextTestCommand : DotnetCommand
        {
            public DependencyContextTestCommand(string dotnetUnderTest) : base(dotnetUnderTest)
            {
            }

            public override CommandResult Execute(string path)
            {
                var args = $"dependency-context-test {path}";
                return base.Execute(args);
            }

            public override CommandResult ExecuteWithCapturedOutput(string path)
            {
                var args = $"dependency-context-test {path}";
                return base.ExecuteWithCapturedOutput(args);
            }
        }
    }
}
