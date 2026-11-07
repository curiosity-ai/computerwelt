"""logging: the standard logging facility, over the sandbox's own streams.

Loggers form the usual dotted hierarchy under a root logger, records propagate to their
ancestors' handlers, and formatting uses the classic %-style record attributes. A
StreamHandler writes to sys.stderr by default; a FileHandler writes through open(), so a
log file lands in the virtual filesystem. Threads, processes and network handlers do not
exist here.
"""

import sys
import time
import traceback

CRITICAL = 50
FATAL = CRITICAL
ERROR = 40
WARNING = 30
WARN = WARNING
INFO = 20
DEBUG = 10
NOTSET = 0

_levelToName = {
    CRITICAL: 'CRITICAL',
    ERROR: 'ERROR',
    WARNING: 'WARNING',
    INFO: 'INFO',
    DEBUG: 'DEBUG',
    NOTSET: 'NOTSET',
}

_nameToLevel = {
    'CRITICAL': CRITICAL,
    'FATAL': FATAL,
    'ERROR': ERROR,
    'WARN': WARNING,
    'WARNING': WARNING,
    'INFO': INFO,
    'DEBUG': DEBUG,
    'NOTSET': NOTSET,
}

BASIC_FORMAT = '%(levelname)s:%(name)s:%(message)s'

_disable = 0


def getLevelName(level):
    if level in _levelToName:
        return _levelToName[level]
    if level in _nameToLevel:
        return _nameToLevel[level]
    return 'Level %s' % level


def addLevelName(level, levelName):
    _levelToName[level] = levelName
    _nameToLevel[levelName] = level


def _checkLevel(level):
    if isinstance(level, int):
        return level
    if isinstance(level, str):
        if level not in _nameToLevel:
            raise ValueError('Unknown level: %r' % level)
        return _nameToLevel[level]
    raise TypeError('Level not an integer or a valid string: %r' % (level,))


class LogRecord:
    def __init__(self, name, level, pathname, lineno, msg, args, exc_info, func=None, sinfo=None):
        self.name = name
        self.msg = msg
        if args and len(args) == 1 and isinstance(args[0], dict) and args[0]:
            args = args[0]
        self.args = args
        self.levelname = getLevelName(level)
        self.levelno = level
        self.pathname = pathname
        self.filename = pathname
        self.module = pathname
        self.lineno = lineno
        self.funcName = func
        self.exc_info = exc_info
        self.exc_text = None
        self.stack_info = sinfo
        self.created = time.time()
        self.msecs = int((self.created - int(self.created)) * 1000)
        self.relativeCreated = 0.0
        self.thread = 0
        self.threadName = 'MainThread'
        self.process = 0
        self.processName = 'MainProcess'
        self.taskName = None

    def getMessage(self):
        msg = str(self.msg)
        if self.args:
            msg = msg % self.args
        return msg

    def __repr__(self):
        return '<LogRecord: %s, %s, %s, %s, "%s">' % (self.name, self.levelno, self.pathname, self.lineno, self.msg)


class PercentStyle:
    default_format = '%(message)s'

    def __init__(self, fmt):
        self._fmt = fmt or self.default_format

    def usesTime(self):
        return '%(asctime)' in self._fmt

    def format(self, record):
        return self._fmt % record.__dict__


class StrFormatStyle(PercentStyle):
    default_format = '{message}'

    def usesTime(self):
        return '{asctime' in self._fmt

    def format(self, record):
        return self._fmt.format(**record.__dict__)


_STYLES = {'%': PercentStyle, '{': StrFormatStyle}


class Formatter:
    default_time_format = '%Y-%m-%d %H:%M:%S'
    default_msec_format = '%s,%03d'

    def __init__(self, fmt=None, datefmt=None, style='%', validate=True, defaults=None):
        if style not in _STYLES:
            raise ValueError('Style must be one of: %s' % ','.join(_STYLES.keys()))
        self._style = _STYLES[style](fmt)
        self._fmt = self._style._fmt
        self.datefmt = datefmt
        self._defaults = defaults or {}

    def formatTime(self, record, datefmt=None):
        ct = time.localtime(record.created)
        if datefmt:
            return time.strftime(datefmt, ct)
        s = time.strftime(self.default_time_format, ct)
        return self.default_msec_format % (s, record.msecs)

    def formatException(self, ei):
        if isinstance(ei, tuple):
            ei = ei[1]
        return ''.join(traceback.format_exception(ei)).rstrip('\n')

    def formatStack(self, stack_info):
        return stack_info

    def usesTime(self):
        return self._style.usesTime()

    def formatMessage(self, record):
        for key, value in self._defaults.items():
            if not hasattr(record, key):
                setattr(record, key, value)
        return self._style.format(record)

    def format(self, record):
        record.message = record.getMessage()
        if self.usesTime():
            record.asctime = self.formatTime(record, self.datefmt)
        s = self.formatMessage(record)
        if record.exc_info:
            if not record.exc_text:
                record.exc_text = self.formatException(record.exc_info)
        if record.exc_text:
            if s[-1:] != '\n':
                s = s + '\n'
            s = s + record.exc_text
        if record.stack_info:
            if s[-1:] != '\n':
                s = s + '\n'
            s = s + self.formatStack(record.stack_info)
        return s


_defaultFormatter = Formatter()


class Filter:
    def __init__(self, name=''):
        self.name = name
        self.nlen = len(name)

    def filter(self, record):
        if self.nlen == 0:
            return True
        if self.name == record.name:
            return True
        if not record.name.startswith(self.name):
            return False
        return record.name[self.nlen] == '.'


class Filterer:
    def __init__(self):
        self.filters = []

    def addFilter(self, filter):
        if filter not in self.filters:
            self.filters.append(filter)

    def removeFilter(self, filter):
        if filter in self.filters:
            self.filters.remove(filter)

    def filter(self, record):
        for f in self.filters:
            if hasattr(f, 'filter'):
                result = f.filter(record)
            else:
                result = f(record)
            if not result:
                return False
            if isinstance(result, LogRecord):
                record = result
        return record


class Handler(Filterer):
    def __init__(self, level=NOTSET):
        Filterer.__init__(self)
        self._name = None
        self.level = _checkLevel(level)
        self.formatter = None

    def get_name(self):
        return self._name

    def set_name(self, name):
        self._name = name

    name = property(get_name, set_name)

    def setLevel(self, level):
        self.level = _checkLevel(level)

    def format(self, record):
        fmt = self.formatter if self.formatter else _defaultFormatter
        return fmt.format(record)

    def emit(self, record):
        raise NotImplementedError('emit must be implemented by Handler subclasses')

    def handle(self, record):
        rv = self.filter(record)
        if rv:
            if isinstance(rv, LogRecord):
                record = rv
            self.emit(record)
        return rv

    def setFormatter(self, fmt):
        self.formatter = fmt

    def flush(self):
        pass

    def close(self):
        pass

    def handleError(self, record):
        sys.stderr.write('--- Logging error ---\n')

    def __repr__(self):
        return '<%s (%s)>' % (self.__class__.__name__, getLevelName(self.level))


class StreamHandler(Handler):
    terminator = '\n'

    def __init__(self, stream=None):
        Handler.__init__(self)
        if stream is None:
            stream = sys.stderr
        self.stream = stream

    def flush(self):
        if hasattr(self.stream, 'flush'):
            self.stream.flush()

    def emit(self, record):
        msg = self.format(record)
        self.stream.write(msg + self.terminator)
        self.flush()

    def setStream(self, stream):
        if stream is self.stream:
            return None
        result = self.stream
        self.stream = stream
        return result


class FileHandler(StreamHandler):
    def __init__(self, filename, mode='a', encoding=None, delay=False, errors=None):
        self.baseFilename = filename
        self.mode = mode
        self.encoding = encoding
        self._file = None
        Handler.__init__(self)
        self.stream = None
        if not delay:
            self.stream = self._open()

    def _open(self):
        return open(self.baseFilename, self.mode, encoding=self.encoding)

    def emit(self, record):
        if self.stream is None:
            self.stream = self._open()
        StreamHandler.emit(self, record)

    def flush(self):
        pass

    def close(self):
        if self.stream is not None and hasattr(self.stream, 'close'):
            self.stream.close()
        self.stream = None


class NullHandler(Handler):
    def handle(self, record):
        pass

    def emit(self, record):
        pass


class _StderrHandler(StreamHandler):
    def __init__(self, level=NOTSET):
        Handler.__init__(self, level)
        self.stream = sys.stderr


lastResort = _StderrHandler(WARNING)


class PlaceHolder:
    def __init__(self, alogger):
        self.loggerMap = {alogger: None}

    def append(self, alogger):
        if alogger not in self.loggerMap:
            self.loggerMap[alogger] = None


class Logger(Filterer):
    def __init__(self, name, level=NOTSET):
        Filterer.__init__(self)
        self.name = name
        self.level = _checkLevel(level)
        self.parent = None
        self.propagate = True
        self.handlers = []
        self.disabled = False

    def setLevel(self, level):
        self.level = _checkLevel(level)

    def debug(self, msg, *args, **kwargs):
        if self.isEnabledFor(DEBUG):
            self._log(DEBUG, msg, args, **kwargs)

    def info(self, msg, *args, **kwargs):
        if self.isEnabledFor(INFO):
            self._log(INFO, msg, args, **kwargs)

    def warning(self, msg, *args, **kwargs):
        if self.isEnabledFor(WARNING):
            self._log(WARNING, msg, args, **kwargs)

    def warn(self, msg, *args, **kwargs):
        self.warning(msg, *args, **kwargs)

    def error(self, msg, *args, **kwargs):
        if self.isEnabledFor(ERROR):
            self._log(ERROR, msg, args, **kwargs)

    def exception(self, msg, *args, exc_info=True, **kwargs):
        self.error(msg, *args, exc_info=exc_info, **kwargs)

    def critical(self, msg, *args, **kwargs):
        if self.isEnabledFor(CRITICAL):
            self._log(CRITICAL, msg, args, **kwargs)

    fatal = critical

    def log(self, level, msg, *args, **kwargs):
        if not isinstance(level, int):
            raise TypeError('level must be an integer')
        if self.isEnabledFor(level):
            self._log(level, msg, args, **kwargs)

    def makeRecord(self, name, level, fn, lno, msg, args, exc_info, func=None, extra=None, sinfo=None):
        rv = LogRecord(name, level, fn, lno, msg, args, exc_info, func, sinfo)
        if extra is not None:
            for key in extra:
                if key in ('message', 'asctime') or key in rv.__dict__:
                    raise KeyError('Attempt to overwrite %r in LogRecord' % key)
                setattr(rv, key, extra[key])
        return rv

    def _log(self, level, msg, args, exc_info=None, extra=None, stack_info=False, stacklevel=1):
        if exc_info:
            if isinstance(exc_info, BaseException):
                exc_info = (type(exc_info), exc_info, None)
            elif not isinstance(exc_info, tuple):
                exc_info = sys.exc_info()
            if exc_info[1] is None:
                exc_info = None
        sinfo = None
        if stack_info:
            sinfo = 'Stack (most recent call last):\n' + ''.join(traceback.format_stack())
        record = self.makeRecord(self.name, level, '<sandbox>', 0, msg, args, exc_info, None, extra, sinfo)
        self.handle(record)

    def handle(self, record):
        if self.disabled:
            return
        maybe = self.filter(record)
        if not maybe:
            return
        if isinstance(maybe, LogRecord):
            record = maybe
        self.callHandlers(record)

    def addHandler(self, hdlr):
        if hdlr not in self.handlers:
            self.handlers.append(hdlr)

    def removeHandler(self, hdlr):
        if hdlr in self.handlers:
            self.handlers.remove(hdlr)

    def hasHandlers(self):
        c = self
        while c:
            if c.handlers:
                return True
            if not c.propagate:
                break
            c = c.parent
        return False

    def callHandlers(self, record):
        c = self
        found = 0
        while c:
            for hdlr in c.handlers:
                found = found + 1
                if record.levelno >= hdlr.level:
                    hdlr.handle(record)
            if not c.propagate:
                c = None
            else:
                c = c.parent
        if found == 0:
            if record.levelno >= lastResort.level:
                lastResort.handle(record)

    def getEffectiveLevel(self):
        logger = self
        while logger:
            if logger.level:
                return logger.level
            logger = logger.parent
        return NOTSET

    def isEnabledFor(self, level):
        if self.disabled or _disable >= level:
            return False
        return level >= self.getEffectiveLevel()

    def getChild(self, suffix):
        if self.root is not self:
            suffix = '.'.join((self.name, suffix))
        return self.manager.getLogger(suffix)

    def __repr__(self):
        level = getLevelName(self.getEffectiveLevel())
        return '<%s %s (%s)>' % (self.__class__.__name__, self.name, level)


class RootLogger(Logger):
    def __init__(self, level):
        Logger.__init__(self, 'root', level)


class Manager:
    def __init__(self, rootnode):
        self.root = rootnode
        self.loggerDict = {}

    def getLogger(self, name):
        if not isinstance(name, str):
            raise TypeError('A logger name must be a string')
        rv = self.loggerDict.get(name)
        if isinstance(rv, Logger):
            return rv
        placeholder = rv
        rv = Logger(name)
        rv.manager = self
        rv.root = self.root
        self.loggerDict[name] = rv
        if isinstance(placeholder, PlaceHolder):
            self._fixupChildren(placeholder, rv)
        self._fixupParents(rv)
        return rv

    def _fixupParents(self, alogger):
        name = alogger.name
        i = name.rfind('.')
        rv = None
        while i > 0 and not rv:
            substr = name[:i]
            if substr not in self.loggerDict:
                self.loggerDict[substr] = PlaceHolder(alogger)
            else:
                obj = self.loggerDict[substr]
                if isinstance(obj, Logger):
                    rv = obj
                else:
                    obj.append(alogger)
            i = name.rfind('.', 0, i - 1)
        if not rv:
            rv = self.root
        alogger.parent = rv

    def _fixupChildren(self, ph, alogger):
        name = alogger.name
        namelen = len(name)
        for c in ph.loggerMap.keys():
            if c.parent.name[:namelen] != name:
                alogger.parent = c.parent
                c.parent = alogger


root = RootLogger(WARNING)
Logger.root = root
Logger.manager = Manager(root)
root.manager = Logger.manager
root.root = root


def getLogger(name=None):
    if not name or (isinstance(name, str) and name == root.name):
        return root
    return Logger.manager.getLogger(name)


def basicConfig(**kwargs):
    force = kwargs.pop('force', False)
    if force:
        for h in root.handlers[:]:
            root.removeHandler(h)
            h.close()
    if len(root.handlers) == 0:
        handlers = kwargs.pop('handlers', None)
        stream = kwargs.pop('stream', None)
        filename = kwargs.pop('filename', None)
        if handlers is None:
            if filename:
                mode = kwargs.pop('filemode', 'a')
                h = FileHandler(filename, mode, encoding=kwargs.pop('encoding', None))
            else:
                h = StreamHandler(stream)
            handlers = [h]
        dfs = kwargs.pop('datefmt', None)
        style = kwargs.pop('style', '%')
        fs = kwargs.pop('format', BASIC_FORMAT if style == '%' else '{levelname}:{name}:{message}')
        fmt = Formatter(fs, dfs, style)
        for h in handlers:
            if h.formatter is None:
                h.setFormatter(fmt)
            root.addHandler(h)
        level = kwargs.pop('level', None)
        if level is not None:
            root.setLevel(level)
        kwargs.pop('filemode', None)
        kwargs.pop('encoding', None)
        kwargs.pop('errors', None)
        if kwargs:
            keys = ', '.join(kwargs.keys())
            raise ValueError('Unrecognised argument(s): %s' % keys)


def _ensure_root():
    if len(root.handlers) == 0:
        basicConfig()


def critical(msg, *args, **kwargs):
    _ensure_root()
    root.critical(msg, *args, **kwargs)


fatal = critical


def error(msg, *args, **kwargs):
    _ensure_root()
    root.error(msg, *args, **kwargs)


def exception(msg, *args, exc_info=True, **kwargs):
    error(msg, *args, exc_info=exc_info, **kwargs)


def warning(msg, *args, **kwargs):
    _ensure_root()
    root.warning(msg, *args, **kwargs)


def warn(msg, *args, **kwargs):
    warning(msg, *args, **kwargs)


def info(msg, *args, **kwargs):
    _ensure_root()
    root.info(msg, *args, **kwargs)


def debug(msg, *args, **kwargs):
    _ensure_root()
    root.debug(msg, *args, **kwargs)


def log(level, msg, *args, **kwargs):
    _ensure_root()
    root.log(level, msg, *args, **kwargs)


def disable(level=CRITICAL):
    global _disable
    _disable = level


def shutdown():
    for h in root.handlers:
        h.flush()


class LoggerAdapter:
    def __init__(self, logger, extra=None):
        self.logger = logger
        self.extra = extra

    def process(self, msg, kwargs):
        kwargs['extra'] = self.extra
        return msg, kwargs

    def log(self, level, msg, *args, **kwargs):
        if self.isEnabledFor(level):
            msg, kwargs = self.process(msg, kwargs)
            self.logger.log(level, msg, *args, **kwargs)

    def debug(self, msg, *args, **kwargs):
        self.log(DEBUG, msg, *args, **kwargs)

    def info(self, msg, *args, **kwargs):
        self.log(INFO, msg, *args, **kwargs)

    def warning(self, msg, *args, **kwargs):
        self.log(WARNING, msg, *args, **kwargs)

    def error(self, msg, *args, **kwargs):
        self.log(ERROR, msg, *args, **kwargs)

    def exception(self, msg, *args, exc_info=True, **kwargs):
        self.log(ERROR, msg, *args, exc_info=exc_info, **kwargs)

    def critical(self, msg, *args, **kwargs):
        self.log(CRITICAL, msg, *args, **kwargs)

    def isEnabledFor(self, level):
        return self.logger.isEnabledFor(level)

    def setLevel(self, level):
        self.logger.setLevel(level)

    def getEffectiveLevel(self):
        return self.logger.getEffectiveLevel()
