using System;
using System.Collections.Generic;

namespace Python.Runtime.Mixins;

/// <summary>Makes serializable .NET types picklable from Python.</summary>
class SerializationMixinsProvider : IPythonBaseTypeProvider, IDisposable
{
    readonly Lazy<PyObject> mixinsModule;

    public SerializationMixinsProvider(Lazy<PyObject> mixinsModule) =>
        this.mixinsModule = mixinsModule ?? throw new ArgumentNullException(nameof(mixinsModule));

    public PyObject Mixins => mixinsModule.Value;

    public IEnumerable<PyType> GetBaseTypes(Type type, IList<PyType> existingBases)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));

        if (existingBases is null)
            throw new ArgumentNullException(nameof(existingBases));

        // add once at the top-most serializable type; subclasses inherit it
        if (type.IsInterface
            || !PickleSupport.IsPicklable(type)
            || (type.BaseType is not null && PickleSupport.IsPicklable(type.BaseType)))
        {
            return existingBases;
        }

        return new List<PyType>(existingBases)
        {
            new(Mixins.GetAttr("SerializableMixin"))
        };
    }

    public void Dispose()
    {
        if (this.mixinsModule.IsValueCreated)
        {
            this.mixinsModule.Value.Dispose();
        }
    }
}
