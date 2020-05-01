// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using Scriban.Helpers;
using Scriban.Runtime;

namespace Scriban.Syntax
{
    [ScriptSyntax("function call expression", "<target_expression> <arguemnt[0]> ... <arguement[n]>")]
    public partial class ScriptFunctionCall : ScriptExpression
    {
        private ScriptExpression _target;
        private ScriptToken _openParent;
        private ScriptList<ScriptExpression> _arguments;
        private ScriptToken _closeParen;

        // Maximum number of parameters is 64
        // it equals the argMask we are using for matching arguments passed
        // as it is a long, it can only store 64 bits, so we are limiting
        // the number of parameter to simplify the implementation.
        public const int MaximumParameterCount = 64;

        public ScriptFunctionCall()
        {
            Arguments = new ScriptList<ScriptExpression>();
        }

        public ScriptExpression Target
        {
            get => _target;
            set => ParentToThis(ref _target, value);
        }

        public ScriptToken OpenParent
        {
            get => _openParent;
            set => ParentToThis(ref _openParent, value);
        }

        public ScriptList<ScriptExpression> Arguments
        {
            get => _arguments;
            set => ParentToThis(ref _arguments, value);
        }

        public ScriptToken CloseParen
        {
            get => _closeParen;
            set => ParentToThis(ref _closeParen, value);
        }

        public bool ExplicitCall { get; set; }

        public bool TryGetFunctionDeclaration(out ScriptFunction function)
        {
            function = null;
            if (!ExplicitCall) return false;
            if (!(Target is ScriptVariableGlobal name)) return false;
            if (OpenParent == null || CloseParen == null) return false;

            foreach(var arg in Arguments)
            {
                if (!(arg is ScriptVariableGlobal)) return false;
            }

            function = new ScriptFunction
            {
                NameOrDoToken = (ScriptVariable)name.Clone(),
                OpenParen = (ScriptToken)OpenParent.Clone(),
                Parameters = new ScriptList<ScriptVariable>(),
                CloseParen = (ScriptToken)CloseParen.Clone(),
                Span = Span
            };

            foreach (var arg in Arguments)
            {
                var parameter = (ScriptVariableGlobal) arg.Clone();
                function.Parameters.Add(parameter);
            }

            return true;
        }

        public void AddArgument(ScriptExpression argument)
        {
            if (argument == null) throw new ArgumentNullException(nameof(argument));
            Arguments.Add(argument);
            if (CloseParen == null && !argument.Span.IsEmpty)
            {
                Span.End = argument.Span.End;
            }
        }

        public ScriptExpression GetScientificExpression(TemplateContext context)
        {
            // If we are in scientific mode and we have a function which takes arguments, and is not an explicit call (e.g sin(x) rather then sin x)
            // Then we need to rewrite the call to a proper expression.
            if (context.UseScientific && !ExplicitCall && Arguments.Count > 0)
            {
                var rewrite = new ScientificFunctionCallRewriter(1 + Arguments.Count);
                rewrite.Add(Target);
                rewrite.AddRange(Arguments);
                return rewrite.Rewrite(context);
            }
            return this;
        }

        public override object Evaluate(TemplateContext context)
        {
            // Double check if the expression can be rewritten
            var newExpression = GetScientificExpression(context);
            if (newExpression != this)
            {
                return context.Evaluate(newExpression);
            }

            // Invoke evaluate on the target, but don't automatically call the function as if it was a parameterless call.
            var targetFunction = context.Evaluate(Target, true);

            // Throw an exception if the target function is null
            if (targetFunction == null)
            {
                throw new ScriptRuntimeException(Target.Span, $"The function `{Target}` was not found");
            }

            return Call(context, this, targetFunction, context.AllowPipeArguments, Arguments);
        }

        public override void PrintTo(ScriptPrinter printer)
        {
            printer.Write(Target);
            if (OpenParent != null)
            {
                printer.Write(OpenParent);
                printer.WriteListWithCommas(Arguments);
            }
            else
            {
                for (var i = 0; i < Arguments.Count; i++)
                {
                    var scriptExpression = Arguments[i];
                    printer.ExpectSpace();
                    printer.Write(scriptExpression);
                }
            }

            if (CloseParen != null) printer.Write(CloseParen);
        }

        public override bool CanHaveLeadingTrivia()
        {
            return false;
        }
        public static bool IsFunction(object target)
        {
            return target is IScriptCustomFunction;
        }

        public static object Call(TemplateContext context, ScriptNode callerContext, object functionObject, bool processPipeArguments, IReadOnlyList<ScriptExpression> arguments)
        {
            if (callerContext == null) throw new ArgumentNullException(nameof(callerContext));
            if (functionObject == null)
            {
                throw new ScriptRuntimeException(callerContext.Span, $"The target function `{callerContext}` is null");
            }
            var scriptFunction = functionObject as ScriptFunction;
            var function = functionObject as IScriptCustomFunction;

            if (function == null)
            {
                throw new ScriptRuntimeException(callerContext.Span, $"Invalid target function `{functionObject}` ({functionObject?.GetType().ScriptFriendlyName()})");
            }

            if (function.ParameterCount >= MaximumParameterCount)
            {
                throw new ScriptRuntimeException(callerContext.Span, $"Out of range number of parameters {function.ParameterCount} for target function `{functionObject}`. The maximum number of parameters for a function is: {MaximumParameterCount}.");
            }

            ScriptBlockStatement blockDelegate = null;
            if (context.BlockDelegates.Count > 0)
            {
                blockDelegate = context.BlockDelegates.Pop();
            }

            // We can't cache this array because it might be collect by the function
            // So we absolutely need to generate a new array everytime we call a function
            ScriptArray argumentValues;

            // Handle pipe arguments here
            if (processPipeArguments && context.CurrentPipeArguments != null && context.CurrentPipeArguments.Count > 0)
            {
                var allArguments = new List<ScriptExpression>();
                var pipeFrom = context.CurrentPipeArguments.Pop();
                argumentValues =  context.GetOrCreateScriptArguments(Math.Max(function.RequiredParameterCount, 1 + (arguments?.Count ?? 0)));
                allArguments.Add(pipeFrom);

                if (arguments != null)
                {
                    allArguments.AddRange(arguments);
                }
                arguments = allArguments;
            }
            else
            {
                argumentValues = context.GetOrCreateScriptArguments(arguments?.Count ?? 0);
            }

            var needLocal = !(function is ScriptFunction func && func.HasParameters);
            object result = null;

            try
            {
                // Process direct arguments
                ulong argMask = 0;
                if (arguments != null)
                {
                    argMask = ProcessArguments(context, callerContext, arguments, function, scriptFunction, argumentValues);
                }

                // Fill remaining argument default values

                FillRemainingOptionalArguments(ref argMask, argumentValues.Count, function.ParameterCount , function, argumentValues);

                var hasVariableParams = function.HasVariableParams;
                var requiredParameterCount = function.RequiredParameterCount;
                var parameterCount = function.ParameterCount;

                // Check the required number of arguments
                var requiredMask = (1U << requiredParameterCount) - 1;
                argMask = argMask & requiredMask;
                if (argMask != requiredMask)
                {
                    int argCount = 0;
                    while (argMask != 0)
                    {
                        if ((argMask & 1) != 0) argCount++;
                        argMask = argMask >> 1;
                    }
                    throw new ScriptRuntimeException(callerContext.Span, $"Invalid number of arguments `{argCount}` passed to `{callerContext}` while expecting `{requiredParameterCount}` arguments");
                }

                if (!hasVariableParams && argumentValues.Count > parameterCount)
                {
                    if (argumentValues.Count > 0 && arguments != null && argumentValues.Count <= arguments.Count)
                    {
                        throw new ScriptRuntimeException(arguments[argumentValues.Count - 1].Span, $"Invalid number of arguments `{argumentValues.Count}` passed to `{callerContext}` while expecting `{parameterCount}` arguments");
                    }
                    throw new ScriptRuntimeException(callerContext.Span, $"Invalid number of arguments `{argumentValues.Count}` passed to `{callerContext}` while expecting `{parameterCount}` arguments");
                }

                context.EnterFunction(callerContext, needLocal);

                try
                {
                    result = function.Invoke(context, callerContext, argumentValues, blockDelegate);
                }
                catch (ArgumentException ex)
                {
                    // Slow path to detect the argument index from the name if we can
                    var index = GetParameterIndexByName(function, ex.ParamName);
                    if (index >= 0 && arguments != null && index < arguments.Count)
                    {
                        throw new ScriptRuntimeException(arguments[index].Span, ex.Message);
                    }

                    throw;
                }
                catch (ScriptArgumentException ex)
                {
                    var index = ex.ArgumentIndex;
                    if (index >= 0 && arguments != null && index < arguments.Count)
                    {
                        throw new ScriptRuntimeException(arguments[index].Span, ex.Message);
                    }
                    throw;
                }
            }
            finally
            {
                context.ReleaseScriptArguments(argumentValues);
                context.ExitFunction(needLocal);
            }

            // Restore the flow state to none
            context.FlowState = ScriptFlowState.None;
            return result;
        }

        private static ulong ProcessArguments(TemplateContext context, ScriptNode callerContext, IReadOnlyList<ScriptExpression> arguments, IScriptCustomFunction function, ScriptFunction scriptFunction, ScriptArray argumentValues)
        {
            bool hasNamedArgument = false;
            ulong argMask = 0;
            var parameterCount = function.ParameterCount;
            for (var argIndex = 0; argIndex < arguments.Count; argIndex++)
            {
                var argument = arguments[argIndex];

                int index = argumentValues.Count;
                object value;

                // Handle named arguments
                var namedArg = argument as ScriptNamedArgument;
                if (namedArg != null)
                {
                    hasNamedArgument = true;
                    var argName = namedArg.Name?.Name;
                    if (argName == null)
                    {
                        throw new ScriptRuntimeException(argument.Span, "Invalid null argument name");
                    }

                    index = GetParameterIndexByName(function, argName);
                    // In case of a ScriptFunction, we write the named argument into the ScriptArray directly
                    if (scriptFunction != null)
                    {
                        if (function.HasVariableParams)
                        {
                            if (index >= 0)
                            {
                            }
                            // We can't add an argument that is "size" for array
                            else if (argumentValues.CanWrite(argName))
                            {
                                argumentValues.SetValue(context, callerContext.Span, argName, context.Evaluate(namedArg), false);
                                continue;
                            }
                            else
                            {
                                throw new ScriptRuntimeException(argument.Span, $"Cannot pass argument {argName} to function. This name is not supported by this function.");
                            }
                        }
                    }

                    if (index < 0)
                    {
                        index = argumentValues.Count;
                    }
                }
                else if (hasNamedArgument)
                {
                    throw new ScriptRuntimeException(argument.Span, "Cannot pass this argument after a named argument");
                }

                if (function.IsParameterType<ScriptExpression>(index))
                {
                    value = argument;
                }
                else
                {
                    value = context.Evaluate(argument);
                }

                // Handle parameters expansion for a function call when the operator ^ is used
                if (argument is ScriptUnaryExpression unaryExpression && unaryExpression.Operator == ScriptUnaryOperator.FunctionParametersExpand && !(value is ScriptExpression))
                {
                    var valueEnumerator = value as IEnumerable;
                    if (valueEnumerator != null)
                    {
                        foreach (var subValue in valueEnumerator)
                        {
                            if (argumentValues.Count < parameterCount) argMask |= 1U << argumentValues.Count;

                            var paramType = function.GetParameterInfo(argumentValues.Count).ParameterType;
                            value = context.ToObject(callerContext.Span, value, paramType);
                            argumentValues.Add(subValue);
                        }
                        continue;
                    }
                }

                {
                    var paramType = function.GetParameterInfo(index).ParameterType;
                    value = context.ToObject(callerContext.Span, value, paramType);
                }

                if (index == argumentValues.Count)
                {
                    if (argumentValues.Count < parameterCount) argMask |= 1U << argumentValues.Count;
                    argumentValues.Add(value);
                    continue;
                }

                // NamedArguments can index further, so we need to fill any intermediate argument values
                // with their default values.
                if (index > argumentValues.Count)
                {
                    FillRemainingOptionalArguments(ref argMask, argumentValues.Count, index, function, argumentValues);
                }

                if (index < parameterCount) argMask |= 1U << index;
                argumentValues[index] = value;
            }

            return argMask;
        }

        private static void FillRemainingOptionalArguments(ref ulong argMask, int startIndex, int length, IScriptCustomFunction function, ScriptArray argumentValues)
        {
            var maxLength = function.ParameterCount;
            if (length > maxLength) length = maxLength;

            for (int i = startIndex; i < length; i++)
            {
                var parameterInfo = function.GetParameterInfo(i);
                if (parameterInfo.HasDefaultValue)
                {
                    argumentValues[i] = parameterInfo.DefaultValue;
                    argMask |= 1U << i;
                }
                else
                {
                    argumentValues[i] = null;
                }
            }
        }

        private static int GetParameterIndexByName(IScriptFunctionInfo functionInfo, string name)
        {
            var paramCount = functionInfo.ParameterCount;
            for (var i = 0; i < paramCount; i++)
            {
                var p = functionInfo.GetParameterInfo(i);
                if (p.Name == name) return i;
            }

            return -1;
        }
    }
}