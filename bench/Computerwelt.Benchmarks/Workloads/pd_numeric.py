import pandas as pd
n = 100_000
df = pd.DataFrame({'k': [i % 20 for i in range(n)], 'x': [i * 0.5 for i in range(n)], 'y': [float(i % 97) for i in range(n)]})
df['z'] = df['x'] * 2.0 + df['y']
s = (df['z'].sum(), df['z'].mean(), df['z'].std(), df['y'].max())
g = df.groupby('k')['z'].mean()
f = df[df['y'] > 50.0]
