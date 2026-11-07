# Extension: `html` — not in upstream Monty.
# escape and unescape follow CPython 3.13, including HTML 5's rules for malformed numeric
# references and for named references written without a semicolon. Expected values
# produced by CPython 3.13.
import html

# === escape ===
assert (html.escape('<a href="x">Tom & \'Jerry\'</a>')) == '&lt;a href=&quot;x&quot;&gt;Tom &amp; &#x27;Jerry&#x27;&lt;/a&gt;', 'html.escape(\'<a href="x">Tom & \\\'Jerry\\\'</a>\')'
assert (html.escape('<a href="x">Tom & \'Jerry\'</a>', quote=False)) == '&lt;a href="x"&gt;Tom &amp; \'Jerry\'&lt;/a&gt;', 'html.escape(\'<a href="x">Tom & \\\'Jerry\\\'</a>\', quote=False)'
assert (html.escape('')) == '', "html.escape('')"
assert (html.escape('plain')) == 'plain', "html.escape('plain')"

# === unescape: named references ===
assert (html.unescape('&lt;p&gt;Fish &amp; Chips&lt;/p&gt;')) == '<p>Fish & Chips</p>', "html.unescape('&lt;p&gt;Fish &amp; Chips&lt;/p&gt;')"
assert (html.unescape('&quot;q&quot; &apos;a&apos; &nbsp;|&copy;&reg;&euro;&hellip;&mdash;')) == '"q" \'a\' \xa0|©®€…—', "html.unescape('&quot;q&quot; &apos;a&apos; &nbsp;|&copy;&reg;&euro;&hellip;&mdash;')"
assert (html.unescape('&Alpha;&beta;&Omega;&rarr;&hearts;&times;&divide;')) == 'ΑβΩ→♥×÷', "html.unescape('&Alpha;&beta;&Omega;&rarr;&hearts;&times;&divide;')"
assert (html.unescape('&copy2023 &amp &ampx &notit; &notin;')) == '©2023 & &x ¬it; ∉', "html.unescape('&copy2023 &amp &ampx &notit; &notin;')"
assert (html.unescape('&bogus; &;&& &#; &#x;')) == '&bogus; &;&& &#; &#x;', "html.unescape('&bogus; &;&& &#; &#x;')"
assert (html.unescape('no references here')) == 'no references here', "html.unescape('no references here')"

# === unescape: numeric references ===
assert (html.unescape('&#62;&#x3e;&#X3E;&#0062;&#x41')) == '>>>>A', "html.unescape('&#62;&#x3e;&#X3E;&#0062;&#x41')"
assert (html.unescape('&#0;&#128;&#150;&#13;')) == '�€–\r', "html.unescape('&#0;&#128;&#150;&#13;')"
assert (html.unescape('&#xD800;&#1114112;&#x110000;')) == '���', "html.unescape('&#xD800;&#1114112;&#x110000;')"
assert (html.unescape('&#1;&#xb;&#xfffe;a')) == 'a', "html.unescape('&#1;&#xb;&#xfffe;a')"
assert (html.unescape('&#128512;&#x1F600;')) == '😀😀', "html.unescape('&#128512;&#x1F600;')"

# === round trip ===
text = 'if a < b && c > "d" then \'e\''
assert (html.unescape(html.escape(text)) == text) == True, 'html.unescape(html.escape(text)) == text'
