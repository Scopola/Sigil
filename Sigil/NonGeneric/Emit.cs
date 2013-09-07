﻿using System;
using Sigil.Impl;
using System.Reflection.Emit;
using System.Reflection;
using System.Collections.Generic;

namespace Sigil.NonGeneric
{
    /// <summary>
    /// Helper for CIL generation that fails as soon as a sequence of instructions
    /// can be shown to be invalid.
    /// 
    /// Unlike Emit&lt;DelegateType&gt;, does not require a known delegate type to construct.
    /// However, if possible use Emit&lt;DelegateType&gt; so as to avoid common type mistakes.
    /// </summary>
    public partial class Emit
    {
        private Emit<NonGenericPlaceholderDelegate> InnerEmit;
        private Module Module;
        private string Name;
        private Type ReturnType;
        private Type[] ParameterTypes;

        private Delegate CreatedDelegate;
        private MethodBuilder CreatedMethod;

        private bool IsDynamicMethod;
        private bool IsMethod;

        private TypeBuilder TypeBuilder;
        private MethodAttributes Attributes;
        private CallingConventions CallingConvention;

        private Emit(Emit<NonGenericPlaceholderDelegate> innerEmit, bool isDynamicMethod, bool isMethod)
        {
            InnerEmit = innerEmit;
            IsDynamicMethod = isDynamicMethod;
            IsMethod = isMethod;
        }

        private static void ValidateReturnAndParameterTypes(Type returnType, Type[] parameterTypes, ValidationOptions validationOptions)
        {
            if (returnType == null)
            {
                throw new ArgumentNullException("returnType");
            }

            if (parameterTypes == null)
            {
                throw new ArgumentNullException("parameterTypes");
            }

            for (var i = 0; i < parameterTypes.Length; i++)
            {
                var parameterType = parameterTypes[i];
                if (parameterType == null)
                {
                    throw new ArgumentException("parameterTypes contains a null reference at index " + i);
                }
            }

            if ((validationOptions & ~ValidationOptions.All) != 0)
            {
                throw new ArgumentException("validationOptions contained unknown flags, found " + validationOptions);
            }
        }

        /// <summary>
        /// Creates a new EmitNonGeneric, optionally using the provided name and module for the inner DynamicMethod.
        /// 
        /// If name is not defined, a sane default is generated.
        /// 
        /// If module is not defined, a module with the same trust as the executing assembly is used instead.
        /// </summary>
        public static Emit NewDynamicMethod(Type returnType, Type[] parameterTypes, string name = null, ModuleBuilder module = null, ValidationOptions validationOptions = ValidationOptions.All)
        {
            ValidateReturnAndParameterTypes(returnType, parameterTypes, validationOptions);

            module = module ?? Emit<NonGenericPlaceholderDelegate>.Module;

            var innerEmit = Emit<NonGenericPlaceholderDelegate>.MakeNonGenericEmit(CallingConventions.Standard, returnType, parameterTypes, Emit<NonGenericPlaceholderDelegate>.AllowsUnverifiableCode(module), validationOptions);

            var ret = new Emit(innerEmit, isDynamicMethod: true, isMethod: false);
            ret.Module = module;
            ret.Name = name ?? AutoNamer.Next("_DynamicMethod");
            ret.ReturnType = returnType;
            ret.ParameterTypes = parameterTypes;

            return ret;
        }

        /// <summary>
        /// Converts the CIL stream into a delegate.
        /// 
        /// Validation that cannot be run until a method is finished is run, and various instructions
        /// are re-written to choose "optimal" forms (Br may become Br_S, for example).
        /// 
        /// Once this method is called the Emit may no longer be modified.
        /// 
        /// `instructions` will be set to a representation of the instructions making up the returned delegate.
        /// Note that this string is typically *not* enough to regenerate the delegate, it is available for
        /// debugging purposes only.  Consumers may find it useful to log the instruction stream in case
        /// the returned delegate fails validation (indicative of a bug in Sigil) or
        /// behaves unexpectedly (indicative of a logic bug in the consumer code).
        /// </summary>
        public Delegate CreateDelegate(Type delegateType, out string instructions, OptimizationOptions optimizationOptions = OptimizationOptions.All)
        {
            if (!IsDynamicMethod)
            {
                throw new InvalidOperationException("Emit was not created to build a DynamicMethod, thus CreateDelegate cannot be called");
            }

            var baseTypes = new LinqHashSet<Type>();
            baseTypes.Add(delegateType);
            var bType = delegateType.BaseType;
            while (bType != null)
            {
                baseTypes.Add(bType);
                bType = bType.BaseType;
            }

            if (!baseTypes.Contains(typeof(Delegate)))
            {
                throw new ArgumentException("delegateType must be a delegate, found " + delegateType.FullName);
            }

            if (InnerEmit.DynMethod == null)
            {
                var dynMethod = new DynamicMethod(Name, ReturnType, ParameterTypes, Module, skipVisibility: true);

                InnerEmit.DynMethod = dynMethod;
            }

            if (CreatedDelegate != null)
            {
                instructions = null;
                return CreatedDelegate;
            }

            CreatedDelegate = InnerEmit.InnerCreateDelegate(delegateType, out instructions, optimizationOptions);

            return CreatedDelegate;
        }

        /// <summary>
        /// Converts the CIL stream into a delegate.
        /// 
        /// Validation that cannot be run until a method is finished is run, and various instructions
        /// are re-written to choose "optimal" forms (Br may become Br_S, for example).
        /// 
        /// Once this method is called the Emit may no longer be modified.
        /// </summary>
        public Delegate CreateDelegate(Type delegateType, OptimizationOptions optimizationOptions = OptimizationOptions.All)
        {
            string ignored;
            return CreateDelegate(delegateType, out ignored, optimizationOptions);
        }

        public DelegateType CreateDelegate<DelegateType>(out string instructions, OptimizationOptions optimizationOptions = OptimizationOptions.All)
        {
            return (DelegateType)(object)CreateDelegate(typeof(DelegateType), out instructions, optimizationOptions);
        }

        public DelegateType CreateDelegate<DelegateType>(OptimizationOptions optimizationOptions = OptimizationOptions.All)
        {
            string ignored;
            return CreateDelegate<DelegateType>(out ignored, optimizationOptions);
        }

        private static bool HasFlag(MethodAttributes value, MethodAttributes flag)
        {
            return (value & flag) != 0;
        }

        private static bool HasFlag(CallingConventions value, CallingConventions flag)
        {
            return (value & flag) != 0;
        }

        private static bool HasFlag(ValidationOptions value, ValidationOptions flag)
        {
            return (value & flag) != 0;
        }

        /// <summary>
        /// Creates a new Emit, suitable for building a method on the given TypeBuilder.
        /// 
        /// The DelegateType and MethodBuilder must agree on return types, parameter types, and parameter counts.
        /// 
        /// If you intend to use unveriable code, you must set allowUnverifiableCode to true.
        /// </summary>
        public static Emit BuildMethod(Type returnType, Type[] parameterTypes, TypeBuilder type, string name, MethodAttributes attributes, CallingConventions callingConvention, bool allowUnverifiableCode = false, ValidationOptions validationOptions = ValidationOptions.All)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }

            if (name == null)
            {
                throw new ArgumentNullException("name");
            }

            Emit<NonGenericPlaceholderDelegate>.CheckAttributesAndConventions(attributes, callingConvention);

            ValidateReturnAndParameterTypes(returnType, parameterTypes, validationOptions);

            if (HasFlag(callingConvention, CallingConventions.HasThis))
            {
                // Shove `this` in front, can't require it because it doesn't exist yet!
                var pList = new List<Type>(parameterTypes);
                pList.Insert(0, type);

                parameterTypes = pList.ToArray();
            }

            var innerEmit = Emit<NonGenericPlaceholderDelegate>.MakeNonGenericEmit(callingConvention, returnType, parameterTypes, allowUnverifiableCode, validationOptions);
            
            var ret = new Emit(innerEmit, isDynamicMethod: false, isMethod: true);
            ret.Name = name;
            ret.ReturnType = returnType;
            ret.ParameterTypes = parameterTypes;
            ret.Attributes = attributes;
            ret.CallingConvention = callingConvention;
            ret.TypeBuilder = type;

            return ret;
        }

        /// <summary>
        /// Writes the CIL stream out to the MethodBuilder used to create this Emit.
        /// 
        /// Validation that cannot be run until a method is finished is run, and various instructions
        /// are re-written to choose "optimal" forms (Br may become Br_S, for example).
        /// 
        /// Once this method is called the Emit may no longer be modified.
        /// 
        /// Returns a MethodBuilder, which can be used to define overrides or for further inspection.
        /// 
        /// `instructions` will be set to a representation of the instructions making up the returned method.
        /// Note that this string is typically *not* enough to regenerate the method, it is available for
        /// debugging purposes only.  Consumers may find it useful to log the instruction stream in case
        /// the returned method fails validation (indicative of a bug in Sigil) or
        /// behaves unexpectedly (indicative of a logic bug in the consumer code).
        /// </summary>
        public MethodBuilder CreateMethod(out string instructions, OptimizationOptions optimizationOptions = OptimizationOptions.All)
        {
            if (!IsMethod)
            {
                throw new InvalidOperationException("Emit was not created to build a method, thus CreateMethod cannot be called");
            }

            if (CreatedMethod != null)
            {
                instructions = null;
                return CreatedMethod;
            }

            var methodBuilder = TypeBuilder.DefineMethod(Name, Attributes, CallingConvention, ReturnType, ParameterTypes);

            InnerEmit.MtdBuilder = methodBuilder;

            CreatedMethod = InnerEmit.CreateMethod(out instructions, optimizationOptions);

            return CreatedMethod;
        }

        /// <summary>
        /// Writes the CIL stream out to the MethodBuilder used to create this Emit.
        /// 
        /// Validation that cannot be run until a method is finished is run, and various instructions
        /// are re-written to choose "optimal" forms (Br may become Br_S, for example).
        /// 
        /// Once this method is called the Emit may no longer be modified.
        /// 
        /// Returns a MethodBuilder, which can be used to define overrides or for further inspection.
        /// </summary>
        public MethodBuilder CreateMethod(OptimizationOptions optimizationOptions = OptimizationOptions.All)
        {
            string ignored;
            return CreateMethod(out ignored, optimizationOptions);
        }
    }
}
