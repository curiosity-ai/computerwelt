"""functools: higher-order functions and operations on callable objects."""

from collections import namedtuple

WRAPPER_ASSIGNMENTS = ('__module__', '__name__', '__qualname__', '__doc__')
WRAPPER_UPDATES = ('__dict__',)

_MISSING = object()


def update_wrapper(wrapper, wrapped, assigned=WRAPPER_ASSIGNMENTS, updated=WRAPPER_UPDATES):
    for attr in assigned:
        try:
            value = getattr(wrapped, attr)
        except AttributeError:
            pass
        else:
            setattr(wrapper, attr, value)
    wrapper.__wrapped__ = wrapped
    return wrapper


def wraps(wrapped, assigned=WRAPPER_ASSIGNMENTS, updated=WRAPPER_UPDATES):
    def decorator(wrapper):
        return update_wrapper(wrapper, wrapped, assigned, updated)
    return decorator


def reduce(function, iterable, initial=_MISSING):
    it = iter(iterable)
    if initial is _MISSING:
        try:
            value = next(it)
        except StopIteration:
            raise TypeError('reduce() of empty iterable with no initial value') from None
    else:
        value = initial
    for element in it:
        value = function(value, element)
    return value


class partial:
    """New function with partial application of the given arguments and keywords."""

    def __init__(self, func, *args, **keywords):
        if not callable(func):
            raise TypeError('the first argument must be callable')
        if isinstance(func, partial):
            args = func.args + args
            keywords = {**func.keywords, **keywords}
            func = func.func
        self.func = func
        self.args = args
        self.keywords = keywords

    def __call__(self, *args, **keywords):
        keywords = {**self.keywords, **keywords}
        return self.func(*self.args, *args, **keywords)

    def __repr__(self):
        parts = [repr(self.func)]
        parts.extend(repr(x) for x in self.args)
        parts.extend(f'{k}={v!r}' for (k, v) in self.keywords.items())
        return f'functools.partial({", ".join(parts)})'


CacheInfo = namedtuple('CacheInfo', ['hits', 'misses', 'maxsize', 'currsize'])


def _make_key(args, kwds, typed, fasttypes={int, str}):
    key = args
    if kwds:
        key += (_MISSING,)
        for item in kwds.items():
            key += item
    if typed:
        key += tuple(type(v) for v in args)
        if kwds:
            key += tuple(type(v) for v in kwds.values())
    elif len(key) == 1 and type(key[0]) in fasttypes:
        return key[0]
    return key


def lru_cache(maxsize=128, typed=False):
    """Least-recently-used cache decorator; maxsize=None means unbounded."""
    if callable(maxsize) and isinstance(typed, bool):
        user_function, maxsize = maxsize, 128
        return _lru_wrapper(user_function, maxsize, typed)
    if maxsize is not None and not isinstance(maxsize, int):
        raise TypeError('Expected first argument to be an integer, a callable, or None')

    def decorating_function(user_function):
        return _lru_wrapper(user_function, maxsize, typed)
    return decorating_function


def _lru_wrapper(user_function, maxsize, typed):
    if maxsize is not None and maxsize < 0:
        maxsize = 0
    cache = {}
    stats = [0, 0]

    def wrapper(*args, **kwds):
        if maxsize == 0:
            stats[1] += 1
            return user_function(*args, **kwds)
        key = _make_key(args, kwds, typed)
        if key in cache:
            stats[0] += 1
            result = cache.pop(key)
            cache[key] = result
            return result
        stats[1] += 1
        result = user_function(*args, **kwds)
        cache[key] = result
        if maxsize is not None and len(cache) > maxsize:
            del cache[next(iter(cache))]
        return result

    def cache_info():
        return CacheInfo(stats[0], stats[1], maxsize, len(cache))

    def cache_clear():
        cache.clear()
        stats[0] = 0
        stats[1] = 0

    wrapper.cache_info = cache_info
    wrapper.cache_clear = cache_clear
    wrapper.cache_parameters = lambda: {'maxsize': maxsize, 'typed': typed}
    return update_wrapper(wrapper, user_function)


def cache(user_function):
    """Simple unbounded cache."""
    return _lru_wrapper(user_function, None, False)


def cmp_to_key(mycmp):
    """Convert a cmp= function into a key= function."""

    class K:
        def __init__(self, obj):
            self.obj = obj

        def __lt__(self, other):
            return mycmp(self.obj, other.obj) < 0

        def __gt__(self, other):
            return mycmp(self.obj, other.obj) > 0

        def __eq__(self, other):
            return mycmp(self.obj, other.obj) == 0

        def __le__(self, other):
            return mycmp(self.obj, other.obj) <= 0

        def __ge__(self, other):
            return mycmp(self.obj, other.obj) >= 0

    return K
