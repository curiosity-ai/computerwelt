"""warnings: CPython 3.13's warning filters, written over the sandbox's standard error.

``warn`` finds the calling frame (``stacklevel`` included), matches the filters in order and
then ignores, raises, or shows the warning as ``file:line: Category: message`` — once per
location for the ``default`` action, as CPython does. ``catch_warnings(record=True)`` collects
``WarningMessage`` objects instead of printing them.

Two differences, both from the interpreter: the line of source CPython prints under the
message is not printed (there is no ``linecache``), and assigning ``warnings.showwarning``
does not redirect output, because a module here cannot be assigned to — use
``catch_warnings(record=True)``.
"""

import re as _re
import sys as _sys
from _warnings import location as _location

__all__ = ["warn", "warn_explicit", "showwarning",
           "formatwarning", "filterwarnings", "simplefilter",
           "resetwarnings", "catch_warnings", "deprecated"]

_ACTIONS = ("error", "ignore", "always", "all", "default", "module", "once")

filters = [
    ('default', None, DeprecationWarning, _re.compile('__main__'), 0),
    ('ignore', None, DeprecationWarning, None, 0),
    ('ignore', None, PendingDeprecationWarning, None, 0),
    ('ignore', None, ImportWarning, None, 0),
    ('ignore', None, ResourceWarning, None, 0),
]
defaultaction = 'default'
onceregistry = {}

_state = {'version': 1, 'log': None}
_registries = {}


def _filters_mutated():
    _state['version'] = _state['version'] + 1


def formatwarning(message, category, filename, lineno, line=None):
    """Function to format a warning the standard way."""
    s = "%s:%s: %s: %s\n" % (filename, lineno, category.__name__, message)
    if line:
        s += "  %s\n" % line.strip()
    return s


def showwarning(message, category, filename, lineno, file=None, line=None):
    """Hook to write a warning to a file; replace if you like."""
    text = formatwarning(message, category, filename, lineno, line)
    if file is None:
        _sys.stderr.write(text)
    else:
        file.write(text)


class WarningMessage:
    """A record of one warning, as catch_warnings(record=True) collects them."""

    _WARNING_DETAILS = ("message", "category", "filename", "lineno", "file",
                        "line", "source")

    def __init__(self, message, category, filename, lineno, file=None,
                 line=None, source=None):
        self.message = message
        self.category = category
        self.filename = filename
        self.lineno = lineno
        self.file = file
        self.line = line
        self.source = source
        self._category_name = category.__name__ if category else None

    def __str__(self):
        return ("{message : %r, category : %r, filename : %r, lineno : %s, "
                "line : %r}" % (self.message, self._category_name,
                                self.filename, self.lineno, self.line))

    def __repr__(self):
        return '<warnings.WarningMessage object>'


def _add_filter(item, append):
    if not append:
        if item in filters:
            filters.remove(item)
        filters.insert(0, item)
    else:
        if item not in filters:
            filters.append(item)
    _filters_mutated()


def filterwarnings(action, message="", category=Warning, module="", lineno=0,
                   append=False):
    """Insert an entry into the list of warnings filters (at the front)."""
    if action not in _ACTIONS:
        raise ValueError("invalid action: %r" % (action,))
    if not isinstance(message, str):
        raise TypeError("message must be a string")
    if not isinstance(module, str):
        raise TypeError("module must be a string")
    if not isinstance(lineno, int):
        raise TypeError("lineno must be an int")
    if lineno < 0:
        raise ValueError("lineno must be an int >= 0")
    if message or module:
        import re
    if message:
        message = _re.compile(message, _re.I)
    else:
        message = None
    if module:
        module = _re.compile(module)
    else:
        module = None
    _add_filter((action, message, category, module, lineno), append)


def simplefilter(action, category=Warning, lineno=0, append=False):
    """Insert a simple entry into the list of warnings filters (at the front)."""
    if action not in _ACTIONS:
        raise ValueError("invalid action: %r" % (action,))
    if not isinstance(lineno, int):
        raise TypeError("lineno must be an int")
    if lineno < 0:
        raise ValueError("lineno must be an int >= 0")
    _add_filter((action, None, category, None, lineno), append)


def resetwarnings():
    """Clear the list of warning filters, so that no filters are active."""
    del filters[:]
    _filters_mutated()


def _show(message, category, filename, lineno, line):
    log = _state['log']
    if log is not None:
        log.append(WarningMessage(message, category, filename, lineno, None, line))
    else:
        showwarning(message, category, filename, lineno, None, line)


def warn_explicit(message, category, filename, lineno, module=None,
                  registry=None, module_globals=None, source=None):
    lineno = int(lineno)
    if module is None:
        module = filename or "<unknown>"
        if module[-3:].lower() == ".py":
            module = module[:-3]
    if registry is None:
        registry = {}
    if registry.get('version', 0) != _state['version']:
        registry.clear()
        registry['version'] = _state['version']
    if isinstance(message, Warning):
        text = str(message)
        category = type(message)
    else:
        text = message
        message = category(message)
    key = (text, category, lineno)
    if registry.get(key):
        return
    action = defaultaction
    for item in filters:
        action_, msg, cat, mod, ln = item
        if ((msg is None or msg.match(text)) and
                issubclass(category, cat) and
                (mod is None or mod.match(module)) and
                (ln == 0 or lineno == ln)):
            action = action_
            break
    if action == "ignore":
        return
    if action == "error":
        raise message
    if action == "once":
        registry[key] = 1
        oncekey = (text, category)
        if onceregistry.get(oncekey):
            return
        onceregistry[oncekey] = 1
    elif action == "always" or action == "all":
        pass
    elif action == "module":
        registry[key] = 1
        altkey = (text, category, 0)
        if registry.get(altkey):
            return
        registry[altkey] = 1
    elif action == "default":
        registry[key] = 1
    else:
        raise RuntimeError(
            "Unrecognized action (%r) in warnings.filters:\n %s" % (action, item))
    _show(message, category, filename, lineno, None)


def warn(message, category=None, stacklevel=1, source=None, *, skip_file_prefixes=()):
    """Issue a warning, or maybe ignore it or raise an exception."""
    if isinstance(message, Warning):
        category = type(message)
    if category is None:
        category = UserWarning
    if not (isinstance(category, type) and issubclass(category, Warning)):
        raise TypeError("category must be a Warning subclass, "
                        "not '{:s}'".format(type(category).__name__))
    filename, lineno, module = _location(stacklevel)
    registry = _registries.get(module)
    if registry is None:
        registry = {}
        _registries[module] = registry
    warn_explicit(message, category, filename, lineno, module, registry, None, source)


class catch_warnings:
    """Saves the warning filters on entry and restores them on exit.

    With record=True, warnings are collected in the list __enter__ returns rather than
    written to standard error.
    """

    def __init__(self, *, record=False, module=None,
                 action=None, category=Warning, lineno=0, append=False):
        self._record = record
        self._entered = False
        if action is None:
            self._filter = None
        else:
            self._filter = (action, category, lineno, append)

    def __repr__(self):
        args = []
        if self._record:
            args.append("record=True")
        return "%s(%s)" % (type(self).__name__, ", ".join(args))

    def __enter__(self):
        if self._entered:
            raise RuntimeError("Cannot enter %r twice" % self)
        self._entered = True
        self._filters = filters[:]
        self._log = _state['log']
        _filters_mutated()
        if self._filter is not None:
            simplefilter(*self._filter)
        if self._record:
            log = []
            _state['log'] = log
            return log
        return None

    def __exit__(self, *exc_info):
        if not self._entered:
            raise RuntimeError("Cannot exit %r without entering first" % self)
        filters[:] = self._filters
        _filters_mutated()
        _state['log'] = self._log
        return False


def deprecated(message, *, category=DeprecationWarning, stacklevel=1):
    """Decorator marking a function as deprecated: calling it issues the warning."""
    if not isinstance(message, str):
        raise TypeError(
            f"Expected an object of type str for 'message', not {type(message).__name__!r}")

    def decorate(arg):
        def wrapper(*args, **kwargs):
            warn(message, category=category, stacklevel=stacklevel + 1)
            return arg(*args, **kwargs)
        wrapper.__deprecated__ = message
        return wrapper

    return decorate
