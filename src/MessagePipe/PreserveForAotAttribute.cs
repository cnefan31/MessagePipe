using System;

namespace MessagePipe
{
    /// <summary>
    /// Marks a type to be preserved during Native AOT compilation.
    /// Use this attribute on configuration classes or types that need to be 
    /// explicitly registered for AOT scenarios.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class PreserveForAotAttribute : Attribute
    {
        /// <summary>
        /// Optional array of types that should be preserved alongside the marked type.
        /// Use this to ensure generic type arguments are not trimmed.
        /// </summary>
        public Type[] Types { get; }

        public PreserveForAotAttribute(params Type[] types)
        {
            Types = types ?? Array.Empty<Type>();
        }
    }
}
