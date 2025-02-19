﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CoreWCF.BuildTools
{
    public sealed partial class OperationParameterInjectionGenerator
    {
        private sealed class Emitter
        {
            private readonly StringBuilder _builder;

            private class Indentor
            {
                const string ____ = "    ";
                const string ________ = "        ";
                const string ____________ = "            ";
                const string ________________ = "                ";
                const string ____________________ = "                    ";
                const string ________________________ = "                        ";
                const string ____________________________ = "                            ";
                const string ________________________________ = "                                ";
                public int Level { get; private set; } = 0;
                public void Increment()
                {
                    Level++;
                }

                public void Decrement()
                {
                    Level--;
                }

                public override string ToString() => Level switch
                {
                    0 => string.Empty,
                    1 => ____,
                    2 => ________,
                    3 => ____________,
                    4 => ________________,
                    5 => ____________________,
                    6 => ________________________,
                    7 => ____________________________,
                    8 => ________________________________,
                    _ => throw new InvalidOperationException(),
                };
            }

            private readonly OperationParameterInjectionSourceGenerationContext _sourceGenerationContext;
            private readonly SourceGenerationSpec _generationSpec;

            public Emitter(in OperationParameterInjectionSourceGenerationContext sourceGenerationContext, in SourceGenerationSpec generationSpec)
            {
                _sourceGenerationContext = sourceGenerationContext;
                _generationSpec = generationSpec;
                _builder = new StringBuilder();
            }

            public void Emit()
            {
                foreach (var operationContractSpec in _generationSpec.OperationContractSpecs)
                {
                    EmitOperationContract(operationContractSpec);
                }
            }

            private void EmitOperationContract(OperationContractSpec operationContractSpec)
            {
                string fileName = $"{operationContractSpec.ServiceContract!.ContainingNamespace.ToDisplayString().Replace(".", "_")}_{operationContractSpec.ServiceContract.Name}_{operationContractSpec.MissingOperationContract!.Name}.g.cs";
                var dependencies = operationContractSpec.UserProvidedOperationContractImplementation!.Parameters.Where(x => !operationContractSpec.MissingOperationContract.Parameters.Any(p =>
                       p.IsMatchingParameter(x))).ToArray();

                bool shouldGenerateAsyncAwait = SymbolEqualityComparer.Default.Equals(operationContractSpec.MissingOperationContract.ReturnType, _generationSpec.TaskSymbol)
                    || (operationContractSpec.MissingOperationContract.ReturnType is INamedTypeSymbol symbol &&
                    SymbolEqualityComparer.Default.Equals(symbol.ConstructedFrom, _generationSpec.GenericTaskSymbol));

                Dictionary<ITypeSymbol, string> dependencyNames = new(SymbolEqualityComparer.Default);

                string @async = shouldGenerateAsyncAwait
                    ? "async "
                    : string.Empty;

                string @await = shouldGenerateAsyncAwait
                    ? "await "
                    : string.Empty;

                string @return = (operationContractSpec.MissingOperationContract.ReturnsVoid || SymbolEqualityComparer.Default.Equals(operationContractSpec.MissingOperationContract.ReturnType, _generationSpec.TaskSymbol)) ?
                    string.Empty
                    : "return ";

                string GetAccessibilityModifier(Accessibility accessibility) => accessibility switch
                {
                    Accessibility.Private => "private ",
                    Accessibility.Protected => "protected ",
                    Accessibility.Public => "public ",
                    _ => "internal "
                };

                string returnType = operationContractSpec.MissingOperationContract.ReturnsVoid
                    ? "void"
                    : $"{operationContractSpec.MissingOperationContract.ReturnType}";

                string parameters = string.Join(", ", operationContractSpec.MissingOperationContract.Parameters
                    .Select(static p => p.RefKind switch
                    {
                        RefKind.Ref => $"ref {p.Type} {p.Name}",
                        RefKind.Out => $"out {p.Type} {p.Name}",
                        _ => $"{p.Type} {p.Name}",
                    }));

                var indentor = new Indentor();
                _builder.Clear();
                _builder.AppendLine($@"
using System;
using Microsoft.Extensions.DependencyInjection;
namespace {operationContractSpec.ServiceContractImplementation!.ContainingNamespace}
{{");
                Stack<INamedTypeSymbol> classes = new();
                INamedTypeSymbol containingType = operationContractSpec.ServiceContractImplementation;
                while (containingType != null)
                {
                    classes.Push(containingType);
                    containingType = containingType.ContainingType;
                }

                while (classes.Count > 0)
                {
                    containingType = classes.Pop();
                    indentor.Increment();
                    _builder.AppendLine($@"{indentor}{GetAccessibilityModifier(containingType.DeclaredAccessibility)}partial class {containingType.Name}");
                    _builder.AppendLine($@"{indentor}{{");
                }

                indentor.Increment();
                _builder.AppendLine($@"{indentor}public {@async}{returnType} {operationContractSpec.MissingOperationContract.Name}({parameters})");
                _builder.AppendLine($@"{indentor}{{");
                indentor.Increment();
                _builder.AppendLine($@"{indentor}var serviceProvider = CoreWCF.OperationContext.Current.InstanceContext.Extensions.Find<IServiceProvider>();");
                _builder.AppendLine($@"{indentor}if (serviceProvider == null) throw new InvalidOperationException(""Missing IServiceProvider in InstanceContext extensions"");");

                if (dependencies.Any(x => SymbolEqualityComparer.Default.Equals(x.Type, operationContractSpec.HttpContextSymbol)
                    || SymbolEqualityComparer.Default.Equals(x.Type, operationContractSpec.HttpRequestSymbol)
                    || SymbolEqualityComparer.Default.Equals(x.Type, operationContractSpec.HttpResponseSymbol)))
                {
                    _builder.AppendLine($@"{indentor}var httpContext = (CoreWCF.OperationContext.Current.RequestContext.RequestMessage.Properties.TryGetValue(""Microsoft.AspNetCore.Http.HttpContext"", out var @object)");
                    indentor.Increment();
                    _builder.AppendLine($@"{indentor}&& @object is Microsoft.AspNetCore.Http.HttpContext context)");
                    _builder.AppendLine($@"{indentor}? context");
                    _builder.AppendLine($@"{indentor}: null;");
                    indentor.Decrement();
                    _builder.AppendLine($@"{indentor}if (httpContext == null) throw new InvalidOperationException(""Missing HttpContext in RequestMessage properties"");");
                }

                _builder.AppendLine($@"{indentor}if (CoreWCF.OperationContext.Current.InstanceContext.IsSingleton)");
                _builder.AppendLine($@"{indentor}{{");
                indentor.Increment();
                _builder.AppendLine($@"{indentor}using (var scope = serviceProvider.CreateScope())");
                _builder.AppendLine($@"{indentor}{{");
                indentor.Increment();

                string dependencyNamePrefix = "d";
                string serviceProviderName = "scope.ServiceProvider";

                AppendResolveDependencies();
                AppendInvokeUserProvidedImplementation();

                if (operationContractSpec.MissingOperationContract.ReturnsVoid || SymbolEqualityComparer.Default.Equals(operationContractSpec.MissingOperationContract.ReturnType, _generationSpec.TaskSymbol))
                {
                    _builder.AppendLine($@"{indentor}return;");
                }

                indentor.Decrement();
                _builder.AppendLine($@"{indentor}}}");
                indentor.Decrement();
                _builder.AppendLine($@"{indentor}}}");

                dependencyNamePrefix = "e";
                serviceProviderName = "serviceProvider";

                AppendResolveDependencies();
                AppendInvokeUserProvidedImplementation();

                while (indentor.Level > 0)
                {
                    indentor.Decrement();
                    _builder.AppendLine($@"{indentor}}}");
                }

                _sourceGenerationContext.AddSource(fileName, SourceText.From(_builder.ToString(), Encoding.UTF8, SourceHashAlgorithm.Sha256));

                void AppendResolveDependencies()
                {
                    for (int i = 0; i < dependencies.Length; i++)
                    {
                        dependencyNames[dependencies[i].Type] = $"{dependencyNamePrefix}{i}";
                        if (SymbolEqualityComparer.Default.Equals(operationContractSpec.HttpContextSymbol, dependencies[i].Type))
                        {
                            _builder.AppendLine($@"{indentor}var {dependencyNamePrefix}{i} = httpContext;");
                        }
                        else if (SymbolEqualityComparer.Default.Equals(operationContractSpec.HttpRequestSymbol, dependencies[i].Type))
                        {
                            _builder.AppendLine($@"{indentor}var {dependencyNamePrefix}{i} = httpContext.Request;");
                        }
                        else if (SymbolEqualityComparer.Default.Equals(operationContractSpec.HttpResponseSymbol, dependencies[i].Type))
                        {
                            _builder.AppendLine($@"{indentor}var {dependencyNamePrefix}{i} = httpContext.Response;");
                        }
                        else
                        {
                            _builder.AppendLine($@"{indentor}var {dependencyNamePrefix}{i} = {serviceProviderName}.GetService<{dependencies[i].Type}>();");
                        }
                    }
                }

                void AppendInvokeUserProvidedImplementation()
                {
                    _builder.Append($"{indentor}{@return}{@await}{operationContractSpec.UserProvidedOperationContractImplementation.Name}(");
                    for (int i = 0; i < operationContractSpec.UserProvidedOperationContractImplementation.Parameters.Length; i++)
                    {
                        IParameterSymbol parameter = operationContractSpec.UserProvidedOperationContractImplementation.Parameters[i];
                        if (i != 0)
                        {
                            _builder.Append(", ");
                        }

                        if (parameter.HasOneOfAttributes(_generationSpec.CoreWCFInjectedSymbol, _generationSpec.MicrosoftAspNetCoreMvcFromServicesSymbol))
                        {
                            _builder.Append(dependencyNames[parameter.Type]);
                        }
                        else
                        {
                            _builder.Append(parameter.RefKind switch
                            {
                                RefKind.Ref => $"ref {parameter.Name}",
                                RefKind.Out => $"out {parameter.Name}",
                                _ => parameter.Name,
                            });
                        }
                    }
                    _builder.AppendLine(");");
                }
            }
        }
    }
}
