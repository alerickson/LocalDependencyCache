// Polyfills for compiler-recognized types that exist in .NET 5+ but not in netstandard2.0.
//
// This library targets netstandard2.0 so PSResourceGet (.NET Framework 4.7.2) can reference it,
// but it is compiled with the latest C# version. Some C# features only compile when specific
// types exist; the compiler finds them by full name, from any assembly, including this one:
//
//   IsExternalInit                     - records (PackageIdentity, PackageDependency)
//   CallerArgumentExpressionAttribute  - automatic parameter names in Guard
//   NotNullWhenAttribute               - nullability of VersionRangeParser.TryParse's out parameter
//   NotNullAttribute                   - nullability after Guard checks
//
// They are internal, so consumers never see them and they cannot conflict with the framework's
// own definitions. They carry no runtime behavior. Remove this file if the library ever drops
// netstandard2.0.

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;

        public string ParameterName { get; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        public bool ReturnValue { get; }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullAttribute : Attribute
    {
    }
}
