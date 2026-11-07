# Extension: logging (written in Python: hierarchy, handlers, formatters,
# basicConfig, exception()) and traceback / sys.exc_info. Absent upstream.
import logging, io, traceback, sys
buf = io.StringIO()
logging.basicConfig(stream=buf, level=logging.INFO, format='%(levelname)s %(name)s: %(message)s')
log = logging.getLogger('app.db')
log.debug('hidden')
log.info('connected to %s', 'db1')
log.warning('slow: %d ms', 250)
try:
    1 / 0
except ZeroDivisionError:
    log.exception('failed')
    tb = traceback.format_exc()
    et, ev, _ = sys.exc_info()
assert et is ZeroDivisionError and 'ZeroDivisionError: division by zero' in tb, tb
out = buf.getvalue().splitlines()
assert out[0] == 'INFO app.db: connected to db1', out
assert out[1] == 'WARNING app.db: slow: 250 ms'
assert out[2] == 'ERROR app.db: failed' and out[-1] == 'ZeroDivisionError: division by zero', out
assert logging.getLogger('app') is log.parent and log.getEffectiveLevel() == logging.INFO
class ListHandler(logging.Handler):
    def __init__(self):
        super().__init__()
        self.records = []
    def emit(self, record):
        self.records.append(self.format(record))
h = ListHandler(); h.setFormatter(logging.Formatter('[%(levelname)s] %(message)s'))
other = logging.getLogger('other'); other.addHandler(h); other.propagate = False; other.setLevel(logging.DEBUG)
other.debug('x=%s', 1)
assert h.records == ['[DEBUG] x=1']
