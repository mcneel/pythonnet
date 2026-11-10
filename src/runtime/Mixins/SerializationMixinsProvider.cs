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
        {
            throw new ArgumentNullException(nameof(type));
        }

        if (existingBases is null)
        {
            throw new ArgumentNullException(nameof(existingBases));
        }

        if (type.IsInterface || !PickleSupport.IsPicklable(type))
        {
            return existingBases;
        }

        // Only the top-most serializable class gets the mixin; its child classes inherit it from there
        bool parentHasMixin = type.BaseType is not null && PickleSupport.IsPicklable(type.BaseType);
        if (parentHasMixin)
        {
            return existingBases;
        }

        var bases = new List<PyType>(existingBases);
        bases.Add(new PyType(Mixins.GetAttr("SerializableMixin")));
        return bases;
    }

    public void Dispose()
    {
        if (this.mixinsModule.IsValueCreated)
        {
            this.mixinsModule.Value.Dispose();
        }
    }
}
