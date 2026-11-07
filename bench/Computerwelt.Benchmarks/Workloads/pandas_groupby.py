import pandas as pd
df = pd.DataFrame({'k': [i % 10 for i in range(2000)], 'v': list(range(2000))})
g = df.groupby('k')['v'].agg(['sum', 'mean'])
f = df[df.v > 100].sort_values('v', ascending=False).head(20)
text = repr(g)
