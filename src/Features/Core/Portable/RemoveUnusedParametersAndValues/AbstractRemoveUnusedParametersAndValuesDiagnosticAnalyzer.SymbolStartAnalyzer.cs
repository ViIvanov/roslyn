﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.RemoveUnusedParametersAndValues
{
    internal abstract partial class AbstractRemoveUnusedParametersAndValuesDiagnosticAnalyzer : AbstractBuiltInCodeStyleDiagnosticAnalyzer
    {
        private sealed partial class SymbolStartAnalyzer
        {
            private readonly AbstractRemoveUnusedParametersAndValuesDiagnosticAnalyzer _compilationAnalyzer;

            private readonly INamedTypeSymbol _eventArgsTypeOpt;
            private readonly ImmutableHashSet<INamedTypeSymbol> _attributeSetForMethodsToIgnore;
            private readonly ConcurrentDictionary<IParameterSymbol, bool> _unusedParameters;
            private readonly ConcurrentDictionary<IMethodSymbol, bool> _methodsUsedAsDelegates;

            public SymbolStartAnalyzer(
                AbstractRemoveUnusedParametersAndValuesDiagnosticAnalyzer compilationAnalyzer,
                INamedTypeSymbol eventArgsTypeOpt,
                ImmutableHashSet<INamedTypeSymbol> attributeSetForMethodsToIgnore)
            {
                _compilationAnalyzer = compilationAnalyzer;

                _eventArgsTypeOpt = eventArgsTypeOpt;
                _attributeSetForMethodsToIgnore = attributeSetForMethodsToIgnore;
                _unusedParameters = new ConcurrentDictionary<IParameterSymbol, bool>();
                _methodsUsedAsDelegates = new ConcurrentDictionary<IMethodSymbol, bool>();
            }

            public static void CreateAndRegisterActions(
                CompilationStartAnalysisContext context,
                AbstractRemoveUnusedParametersAndValuesDiagnosticAnalyzer analyzer)
            {
                var attributeSetForMethodsToIgnore = ImmutableHashSet.CreateRange(GetAttributesForMethodsToIgnore(context.Compilation));
                var eventsArgType = context.Compilation.EventArgsType();
                var symbolAnalyzer = new SymbolStartAnalyzer(analyzer, eventsArgType, attributeSetForMethodsToIgnore);
                context.RegisterSymbolStartAction(symbolAnalyzer.OnSymbolStart, SymbolKind.NamedType);
            }

            private void OnSymbolStart(SymbolStartAnalysisContext context)
            {
                context.RegisterOperationBlockStartAction(OnOperationBlock);
                context.RegisterSymbolEndAction(OnSymbolEnd);
            }

            private void OnOperationBlock(OperationBlockStartAnalysisContext context)
            {
                context.RegisterOperationAction(OnMethodReference, OperationKind.MethodReference);
                BlockAnalyzer.Analyze(context, this);
            }

            private void OnMethodReference(OperationAnalysisContext context)
            {
                var methodBinding = (IMethodReferenceOperation)context.Operation;
                _methodsUsedAsDelegates.GetOrAdd(methodBinding.Method.OriginalDefinition, true);
            }

            private void OnSymbolEnd(SymbolAnalysisContext context)
            {
                foreach (var parameterAndUsageKvp in _unusedParameters)
                {
                    var parameter = parameterAndUsageKvp.Key;
                    bool hasReference = parameterAndUsageKvp.Value;

                    ReportUnusedParameterDiagnostic(parameter, hasReference, context.ReportDiagnostic, context.Options, context.CancellationToken);
                }
            }

            private void ReportUnusedParameterDiagnostic(
                IParameterSymbol parameter,
                bool hasReference,
                Action<Diagnostic> reportDiagnostic,
                AnalyzerOptions analyzerOptions,
                CancellationToken cancellationToken)
            {
                if (!IsUnusedParameterCandidate(parameter))
                {
                    return;
                }

                var location = parameter.Locations[0];
                var optionSet = analyzerOptions.GetDocumentOptionSetAsync(location.SourceTree, cancellationToken).GetAwaiter().GetResult();
                if (optionSet == null)
                {
                    return;
                }

                var option = optionSet.GetOption(CodeStyleOptions.UnusedParameters, parameter.Language);
                if (option.Notification.Severity == ReportDiagnostic.Suppress ||
                    !ShouldReportUnusedParameters(parameter.ContainingSymbol, option.Value, option.Notification.Severity))
                {
                    return;
                }

                var diagnostic = DiagnosticHelper.CreateWithMessage(s_unusedParameterRule, location,
                    option.Notification.Severity, additionalLocations: null, properties: null, message: GetMessage());
                reportDiagnostic(diagnostic);

                return;

                // Local functions.
                LocalizableString GetMessage()
                {
                    LocalizableString messageFormat;
                    if (parameter.ContainingSymbol.HasPublicResultantVisibility() &&
                        !parameter.ContainingSymbol.IsLocalFunction())
                    {
                        messageFormat = hasReference
                            ? FeaturesResources.Remove_unused_parameter_0_if_it_is_not_part_of_a_shipped_public_API_its_initial_value_is_never_used
                            : FeaturesResources.Remove_unused_parameter_0_if_it_is_not_part_of_a_shipped_public_API;
                    }
                    else if (hasReference)
                    {
                        messageFormat = FeaturesResources.Remove_unused_parameter_0_its_initial_value_is_never_used;
                    }
                    else
                    {
                        messageFormat = s_unusedParameterRule.MessageFormat;
                    }

                    return new DiagnosticHelper.LocalizableStringWithArguments(messageFormat, parameter.Name);
                }
            }

            private static IEnumerable<INamedTypeSymbol> GetAttributesForMethodsToIgnore(Compilation compilation)
            {
                // Ignore conditional methods (One conditional will often call another conditional method as its only use of a parameter)
                var conditionalAttribte = compilation.ConditionalAttribute();
                if (conditionalAttribte != null)
                {
                    yield return conditionalAttribte;
                }

                // Ignore methods with special serialization attributes (All serialization methods need to take 'StreamingContext')
                var onDeserializingAttribute = compilation.OnDeserializingAttribute();
                if (onDeserializingAttribute != null)
                {
                    yield return onDeserializingAttribute;
                }

                var onDeserializedAttribute = compilation.OnDeserializedAttribute();
                if (onDeserializedAttribute != null)
                {
                    yield return onDeserializedAttribute;
                }

                var onSerializingAttribute = compilation.OnSerializingAttribute();
                if (onSerializingAttribute != null)
                {
                    yield return onSerializingAttribute;
                }

                var onSerializedAttribute = compilation.OnSerializedAttribute();
                if (onSerializedAttribute != null)
                {
                    yield return onSerializedAttribute;
                }

                // Don't flag obsolete methods.
                var obsoleteAttribute = compilation.ObsoleteAttribute();
                if (obsoleteAttribute != null)
                {
                    yield return obsoleteAttribute;
                }
            }

            private bool IsUnusedParameterCandidate(IParameterSymbol parameter)
            {
                // Ignore certain special parameters/methods.
                // Note that "method.ExplicitOrImplicitInterfaceImplementations" check below is not a complete check,
                // as identifying this correctly requires analyzing referencing projects, which is not
                // supported for analyzers. We believe this is still a good enough check for most cases so 
                // we don't have to bail out on reporting unused parameters for all public methods.

                if (parameter.IsImplicitlyDeclared ||
                    parameter.Name == DiscardVariableName ||
                    !(parameter.ContainingSymbol is IMethodSymbol method) ||
                    method.IsImplicitlyDeclared ||
                    method.IsExtern ||
                    method.IsAbstract ||
                    method.IsVirtual ||
                    method.IsOverride ||
                    !method.ExplicitOrImplicitInterfaceImplementations().IsEmpty ||
                    method.IsAccessor() ||
                    method.IsAnonymousFunction())
                {
                    return false;
                }

                // Ignore event handler methods "Handler(object, MyEventArgs)"
                // as event handlers are required to match this signature
                // regardless of whether or not the parameters are used.
                if (_eventArgsTypeOpt != null &&
                    method.Parameters.Length == 2 &&
                    method.Parameters[0].Type.SpecialType == SpecialType.System_Object &&
                    method.Parameters[1].Type.InheritsFromOrEquals(_eventArgsTypeOpt))
                {
                    return false;
                }

                // Ignore flagging parameters for methods with certain well-known attributes,
                // which are known to have unused parameters in real world code.
                if (method.GetAttributes().Any(a => a.AttributeClass != null && _attributeSetForMethodsToIgnore.Contains(a.AttributeClass)))
                {
                    return false;
                }

                // Methods used as delegates likely need to have unused parameters for signature compat.
                if (_methodsUsedAsDelegates.ContainsKey(method))
                {
                    return false;
                }

                return true;
            }
        }
    }
}
