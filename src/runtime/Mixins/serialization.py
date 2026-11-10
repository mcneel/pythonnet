"""
Implements pickle (and so copy/deepcopy) for serializable .NET types
"""

class SerializableMixin:
    """Pickles .NET objects that implement ISerializable or are marked [Serializable]"""
    def __reduce_ex__(self, protocol):
        import clr
        return (_reconstruct, tuple(clr._pickle_state(self)))

    def __reduce__(self):
        return self.__reduce_ex__(2)


def _reconstruct(type_name, kind, state):
    """Rebuilds a .NET object pickled by SerializableMixin. Must stay importable by this name."""
    import clr
    return clr._unpickle_state(type_name, kind, state)
