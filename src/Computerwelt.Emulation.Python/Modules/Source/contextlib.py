"""contextlib: utilities for with-statement contexts."""


class _GeneratorContextManager:
    def __init__(self, func, args, kwds):
        self.gen = func(*args, **kwds)
        self.func, self.args, self.kwds = func, args, kwds

    def __enter__(self):
        try:
            return next(self.gen)
        except StopIteration:
            raise RuntimeError("generator didn't yield")

    def __exit__(self, typ, value, traceback):
        if typ is None:
            try:
                next(self.gen)
            except StopIteration:
                return False
            else:
                try:
                    raise RuntimeError("generator didn't stop")
                finally:
                    self.gen.close()
        else:
            if value is None:
                value = typ()
            try:
                self.gen.throw(value)
            except StopIteration as exc:
                return exc is not value
            except RuntimeError as exc:
                if exc is value:
                    return False
                raise
            except BaseException as exc:
                if exc is not value:
                    raise
                return False
            try:
                raise RuntimeError("generator didn't stop after throw()")
            finally:
                self.gen.close()


def contextmanager(func):
    """@contextmanager decorator: turn a generator into a context manager factory."""
    def helper(*args, **kwds):
        return _GeneratorContextManager(func, args, kwds)
    helper.__name__ = func.__name__
    helper.__doc__ = func.__doc__
    helper.__wrapped__ = func
    return helper


class closing:
    """Context to automatically close something at the end of a block."""

    def __init__(self, thing):
        self.thing = thing

    def __enter__(self):
        return self.thing

    def __exit__(self, *exc_info):
        self.thing.close()


class suppress:
    """Context manager to suppress specified exceptions."""

    def __init__(self, *exceptions):
        self._exceptions = exceptions

    def __enter__(self):
        pass

    def __exit__(self, exctype, excinst, exctb):
        if exctype is None:
            return False
        return isinstance(excinst, self._exceptions)


class nullcontext:
    """Context manager that does no additional processing."""

    def __init__(self, enter_result=None):
        self.enter_result = enter_result

    def __enter__(self):
        return self.enter_result

    def __exit__(self, *excinfo):
        pass


class ExitStack:
    """Context manager for dynamic management of a stack of exit callbacks."""

    def __init__(self):
        self._exit_callbacks = []

    def pop_all(self):
        new_stack = ExitStack()
        new_stack._exit_callbacks = self._exit_callbacks
        self._exit_callbacks = []
        return new_stack

    def push(self, exit):
        if hasattr(exit, '__exit__'):
            self._exit_callbacks.append(exit.__exit__)
        else:
            self._exit_callbacks.append(exit)
        return exit

    def enter_context(self, cm):
        result = cm.__enter__()
        self._exit_callbacks.append(cm.__exit__)
        return result

    def callback(self, callback, *args, **kwds):
        def _exit_wrapper(exc_type, exc, tb):
            callback(*args, **kwds)
        self._exit_callbacks.append(_exit_wrapper)
        return callback

    def close(self):
        self.__exit__(None, None, None)

    def __enter__(self):
        return self

    def __exit__(self, *exc_details):
        received_exc = exc_details[0] is not None
        suppressed_exc = False
        pending_raise = False
        current = exc_details[1]
        exc_info = exc_details
        while self._exit_callbacks:
            cb = self._exit_callbacks.pop()
            try:
                if cb(*exc_info):
                    suppressed_exc = True
                    pending_raise = False
                    exc_info = (None, None, None)
                    current = None
            except BaseException as new_exc:
                exc_info = (type(new_exc), new_exc, None)
                current = new_exc
                pending_raise = True
                suppressed_exc = False
        if pending_raise:
            raise current
        return received_exc and suppressed_exc
