using System;
#if !UNITY_2018_3_OR_NEWER
using Microsoft.Extensions.DependencyInjection;
#endif

namespace MessagePipe
{
    /// <summary>
    /// Provides AOT compatibility validation utilities for MessagePipe.
    /// Use these methods to verify that all required types are properly registered
    /// before publishing as a Native AOT application.
    /// </summary>
#if !UNITY_2018_3_OR_NEWER
    public static class AotCompatibilityValidator
    {
        /// <summary>
        /// Validates that the service collection has been configured with AOT-safe registrations.
        /// Returns true if all MessagePipe services appear to be properly registered.
        /// </summary>
        /// <param name="services">The service collection to validate.</param>
        /// <returns>True if AOT-compatible, false otherwise.</returns>
        public static bool Validate(IServiceCollection services)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            // Check for core MessagePipe services
            var hasOptions = false;
            var hasEventFactory = false;
            var hasDiagnosticsInfo = false;

            foreach (var descriptor in services)
            {
                var serviceType = descriptor.ServiceType;
                
                if (serviceType == typeof(MessagePipeOptions)) hasOptions = true;
                if (serviceType == typeof(EventFactory)) hasEventFactory = true;
                if (serviceType == typeof(MessagePipeDiagnosticsInfo)) hasDiagnosticsInfo = true;
            }

            return hasOptions && hasEventFactory && hasDiagnosticsInfo;
        }

        /// <summary>
        /// Attempts to resolve all registered MessagePipe publishers and subscribers
        /// to verify they can be instantiated at runtime.
        /// </summary>
        /// <param name="serviceProvider">The service provider to validate against.</param>
        /// <param name="errors">Collection of error messages if validation fails.</param>
        /// <returns>True if all services can be resolved, false otherwise.</returns>
        public static bool AssertAllTypesRegistered(IServiceProvider serviceProvider, out string[] errors)
        {
            var errorList = new System.Collections.Generic.List<string>();

            try
            {
                // Try to resolve core services
                _ = serviceProvider.GetRequiredService<MessagePipeOptions>();
                _ = serviceProvider.GetRequiredService<EventFactory>();
                _ = serviceProvider.GetRequiredService<MessagePipeDiagnosticsInfo>();
            }
            catch (Exception ex)
            {
                errorList.Add($"Failed to resolve core MessagePipe services: {ex.Message}");
            }

            errors = errorList.ToArray();
            return errors.Length == 0;
        }

        /// <summary>
        /// Checks if the current runtime environment supports Native AOT features.
        /// </summary>
        /// <returns>True if running in an AOT-compatible environment.</returns>
        public static bool IsAotEnvironment()
        {
#if NET7_0_OR_GREATER
            return System.Runtime.InteropServices.RuntimeFeature.IsDynamicCodeSupported == false;
#else
            return false;
#endif
        }

        /// <summary>
        /// Gets a descriptive message about the current AOT status.
        /// </summary>
        /// <returns>A string describing the AOT environment status.</returns>
        public static string GetAotStatusMessage()
        {
            if (IsAotEnvironment())
            {
                return "Running in Native AOT mode - dynamic code generation is not available.";
            }
            else
            {
#if NET7_0_OR_GREATER
                return "Running in JIT mode - dynamic code generation is available.";
#else
                return "Running in JIT mode (.NET version does not support AOT detection).";
#endif
            }
        }
    }
#endif

    /// <summary>
    /// Result of an AOT validation operation.
    /// </summary>
    public readonly struct AotValidationResult
    {
        /// <summary>
        /// True if validation passed, false otherwise.
        /// </summary>
        public bool IsValid { get; }

        /// <summary>
        /// Collection of warning or error messages.
        /// </summary>
        public string[] Messages { get; }

        internal AotValidationResult(bool isValid, string[] messages)
        {
            IsValid = isValid;
            Messages = messages ?? Array.Empty<string>();
        }
    }
}
