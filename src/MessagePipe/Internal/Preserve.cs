namespace MessagePipe.Internal
{
    // Preserve for Unity IL2CPP

    internal class PreserveAttribute : System.Attribute
    {
    }

    /// <summary>
    /// Marks a type or member to be preserved during Native AOT compilation.
    /// Use this attribute to ensure types used via reflection or dynamic scenarios
    /// are not trimmed by the IL linker.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct | System.AttributeTargets.Method | System.AttributeTargets.Constructor | System.AttributeTargets.Property | System.AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
    public sealed class DynamicallyAccessedMembersAttribute : System.Attribute
    {
        public DynamicallyAccessedMemberTypes MemberTypes { get; }

        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes)
        {
            MemberTypes = memberTypes;
        }
    }

    /// <summary>
    /// Specifies the types of members that are dynamically accessed.
    /// </summary>
    [System.Flags]
    public enum DynamicallyAccessedMemberTypes
    {
        None = 0,
        PublicParameterlessConstructor = 1,
        PublicConstructors = 3,
        NonPublicConstructors = 5,
        PublicMethods = 7,
        NonPublicMethods = 11,
        PublicFields = 15,
        NonPublicFields = 23,
        PublicNestedTypes = 31,
        NonPublicNestedTypes = 47,
        PublicProperties = 63,
        NonPublicProperties = 95,
        PublicEvents = 127,
        NonPublicEvents = 191,
        Interfaces = 255,
        All = 511
    }

    /// <summary>
    /// Indicates that the specified method requires dynamic code generation features.
    /// </summary>
    public sealed class RequiresDynamicCodeAttribute : System.Attribute
    {
        public string Message { get; }

        public RequiresDynamicCodeAttribute(string message)
        {
            Message = message;
        }
    }

    /// <summary>
    /// Indicates that the specified method requires unreferenced code at runtime.
    /// </summary>
    public sealed class RequiresUnreferencedCodeAttribute : System.Attribute
    {
        public string Message { get; }

        public RequiresUnreferencedCodeAttribute(string message)
        {
            Message = message;
        }
    }

    /// <summary>
    /// Suppresses warnings about dynamically accessed members.
    /// </summary>
    public sealed class UnconditionalSuppressMessageAttribute : System.Attribute
    {
        public string Category { get; }
        public string CheckId { get; }

        public UnconditionalSuppressMessageAttribute(string category, string checkId)
        {
            Category = category;
            CheckId = checkId;
        }
    }
}
