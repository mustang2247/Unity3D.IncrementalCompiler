﻿using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Linq;
using System.Text;
using Mono.CompilerServices.SymbolWriter;
using NLog;

namespace IncrementalCompiler
{
    public class Compiler
    {
        private Logger _logger = LogManager.GetLogger("Compiler");
        private CSharpCompilation _compilation;
        private CompileOptions _options;
        private FileTimeList _referenceFileList;
        private FileTimeList _sourceFileList;
        private Dictionary<string, MetadataReference> _referenceMap;
        private Dictionary<string, SyntaxTree> _sourceMap;

        public CompileResult Build(CompileOptions options)
        {
            if (_compilation == null ||
                _options.WorkDirectory != options.WorkDirectory ||
                _options.AssemblyName != options.AssemblyName ||
                _options.Output != options.Output ||
                _options.Defines.SequenceEqual(options.Defines) == false)
            {
                return BuildFull(options);
            }
            else
            {
                return BuildIncremental(options);
            }
        }

        private CompileResult BuildFull(CompileOptions options)
        {
            var result = new CompileResult();

            _logger.Info("BuildFull");
            _options = options;

            _referenceFileList = new FileTimeList();
            _referenceFileList.Update(options.References);

            _sourceFileList = new FileTimeList();
            _sourceFileList.Update(options.Files);

            _referenceMap = options.References.ToDictionary(
               file => file,
               file => CreateReference(file));

            var parseOption = new CSharpParseOptions(LanguageVersion.CSharp6, DocumentationMode.Parse, SourceCodeKind.Regular, options.Defines);
            _sourceMap = options.Files.ToDictionary(
                file => file,
                file => ParseSource(file, parseOption));

            _compilation = CSharpCompilation.Create(
                options.AssemblyName,
                _sourceMap.Values,
                _referenceMap.Values,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            Emit(result);

            return result;
        }

        private CompileResult BuildIncremental(CompileOptions options)
        {
            var result = new CompileResult();

            _logger.Info("BuildIncremental");
            _options = options;

            // update reference files

            var referenceChanges = _referenceFileList.Update(options.References);
            foreach (var file in referenceChanges.Added)
            {
                _logger.Info("+ {0}", file);
                var reference = CreateReference(file);
                _compilation = _compilation.AddReferences(reference);
                _referenceMap.Add(file, reference);
            }
            foreach (var file in referenceChanges.Changed)
            {
                _logger.Info("* {0}", file);
                var reference = CreateReference(file);
                _compilation = _compilation.RemoveReferences(_referenceMap[file])
                                           .AddReferences(reference);
                _referenceMap[file] = reference;
            }
            foreach (var file in referenceChanges.Removed)
            {
                _logger.Info("- {0}", file);
                _compilation = _compilation.RemoveReferences(_referenceMap[file]);
                _referenceMap.Remove(file);
            }

            // update source files

            var sourceChanges = _sourceFileList.Update(options.Files);
            var parseOption = new CSharpParseOptions(LanguageVersion.CSharp6, DocumentationMode.Parse, SourceCodeKind.Regular, options.Defines);
            foreach (var file in sourceChanges.Added)
            {
                _logger.Info("+ {0}", file);
                var syntaxTree = ParseSource(file, parseOption);
                _compilation = _compilation.AddSyntaxTrees(syntaxTree);
                _sourceMap.Add(file, syntaxTree);
            }
            foreach (var file in sourceChanges.Changed)
            {
                _logger.Info("* {0}", file);
                var syntaxTree = ParseSource(file, parseOption);
                _compilation = _compilation.RemoveSyntaxTrees(_sourceMap[file])
                                           .AddSyntaxTrees(syntaxTree);
                _sourceMap[file] = syntaxTree;
            }
            foreach (var file in sourceChanges.Removed)
            {
                _logger.Info("- {0}", file);
                _compilation = _compilation.RemoveSyntaxTrees(_sourceMap[file]);
                _sourceMap.Remove(file);
            }

            Emit(result);

            return result;
        }

        private MetadataReference CreateReference(string file)
        {
            return MetadataReference.CreateFromFile(Path.Combine(_options.WorkDirectory, file));
        }

        private SyntaxTree ParseSource(string file, CSharpParseOptions parseOption)
        {
            var text = File.ReadAllText(Path.Combine(_options.WorkDirectory, file));
            return CSharpSyntaxTree.ParseText(text, parseOption, file, Encoding.UTF8);
        }

        private void Emit(CompileResult result)
        {
            var outputFile = Path.Combine(_options.WorkDirectory, _options.Output);
            var debugFile = Path.Combine(_options.WorkDirectory, _options.Output + ".mdb");
            using (var peStream = new FileStream(outputFile, FileMode.Create))
            using (var pdbStream = new FileStream(debugFile, FileMode.Create))
            {
                var r = _compilation.EmitWithMdb(peStream, pdbStream);

                foreach (var d in r.Diagnostics)
                {
                    if (d.Severity == DiagnosticSeverity.Warning && d.IsWarningAsError == false)
                        result.Warnings.Add(GetDiagnosticString(d, "warning"));
                    else if (d.Severity == DiagnosticSeverity.Error || d.IsWarningAsError)
                        result.Errors.Add(GetDiagnosticString(d, "error"));
                }

                result.Succeeded = r.Success;
            }
        }

        private static string GetDiagnosticString(Diagnostic diagnostic, string type)
        {
            var line = diagnostic.Location.GetLineSpan();
            return $"{line.Path}({line.StartLinePosition.Line + 1},{line.StartLinePosition.Character + 1}): " + 
                   $"{type} {diagnostic.Id}: {diagnostic.GetMessage()}";
        }
    }
}