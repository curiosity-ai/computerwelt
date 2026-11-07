"""html: escape and unescape, as CPython 3.13 defines them.

``unescape`` follows the HTML 5 rules CPython implements, for numeric references and for
named ones, including the longest-prefix match for a name written without its semicolon.
The named table is the HTML 4 set (every entity ``html.entities.name2codepoint`` has) plus
``&apos;`` and every legacy name the standard accepts without a semicolon; the rest of the
2,231-entry HTML 5 table is not carried.
"""

import re as _re

__all__ = ['escape', 'unescape']


def escape(s, quote=True):
    """Replace "&", "<" and ">" (and, with quote, both quotation marks) with HTML-safe sequences."""
    s = s.replace("&", "&amp;")
    s = s.replace("<", "&lt;")
    s = s.replace(">", "&gt;")
    if quote:
        s = s.replace('"', "&quot;")
        s = s.replace('\'', "&#x27;")
    return s


_html5 = {
    'AElig': 'Æ',
    'AElig;': 'Æ',
    'AMP': '&',
    'Aacute': 'Á',
    'Aacute;': 'Á',
    'Acirc': 'Â',
    'Acirc;': 'Â',
    'Agrave': 'À',
    'Agrave;': 'À',
    'Alpha;': 'Α',
    'Aring': 'Å',
    'Aring;': 'Å',
    'Atilde': 'Ã',
    'Atilde;': 'Ã',
    'Auml': 'Ä',
    'Auml;': 'Ä',
    'Beta;': 'Β',
    'COPY': '©',
    'Ccedil': 'Ç',
    'Ccedil;': 'Ç',
    'Chi;': 'Χ',
    'Dagger;': '‡',
    'Delta;': 'Δ',
    'ETH': 'Ð',
    'ETH;': 'Ð',
    'Eacute': 'É',
    'Eacute;': 'É',
    'Ecirc': 'Ê',
    'Ecirc;': 'Ê',
    'Egrave': 'È',
    'Egrave;': 'È',
    'Epsilon;': 'Ε',
    'Eta;': 'Η',
    'Euml': 'Ë',
    'Euml;': 'Ë',
    'GT': '>',
    'Gamma;': 'Γ',
    'Iacute': 'Í',
    'Iacute;': 'Í',
    'Icirc': 'Î',
    'Icirc;': 'Î',
    'Igrave': 'Ì',
    'Igrave;': 'Ì',
    'Iota;': 'Ι',
    'Iuml': 'Ï',
    'Iuml;': 'Ï',
    'Kappa;': 'Κ',
    'LT': '<',
    'Lambda;': 'Λ',
    'Mu;': 'Μ',
    'Ntilde': 'Ñ',
    'Ntilde;': 'Ñ',
    'Nu;': 'Ν',
    'OElig;': 'Œ',
    'Oacute': 'Ó',
    'Oacute;': 'Ó',
    'Ocirc': 'Ô',
    'Ocirc;': 'Ô',
    'Ograve': 'Ò',
    'Ograve;': 'Ò',
    'Omega;': 'Ω',
    'Omicron;': 'Ο',
    'Oslash': 'Ø',
    'Oslash;': 'Ø',
    'Otilde': 'Õ',
    'Otilde;': 'Õ',
    'Ouml': 'Ö',
    'Ouml;': 'Ö',
    'Phi;': 'Φ',
    'Pi;': 'Π',
    'Prime;': '″',
    'Psi;': 'Ψ',
    'QUOT': '"',
    'REG': '®',
    'Rho;': 'Ρ',
    'Scaron;': 'Š',
    'Sigma;': 'Σ',
    'THORN': 'Þ',
    'THORN;': 'Þ',
    'Tau;': 'Τ',
    'Theta;': 'Θ',
    'Uacute': 'Ú',
    'Uacute;': 'Ú',
    'Ucirc': 'Û',
    'Ucirc;': 'Û',
    'Ugrave': 'Ù',
    'Ugrave;': 'Ù',
    'Upsilon;': 'Υ',
    'Uuml': 'Ü',
    'Uuml;': 'Ü',
    'Xi;': 'Ξ',
    'Yacute': 'Ý',
    'Yacute;': 'Ý',
    'Yuml;': 'Ÿ',
    'Zeta;': 'Ζ',
    'aacute': 'á',
    'aacute;': 'á',
    'acirc': 'â',
    'acirc;': 'â',
    'acute': '´',
    'acute;': '´',
    'aelig': 'æ',
    'aelig;': 'æ',
    'agrave': 'à',
    'agrave;': 'à',
    'alefsym;': 'ℵ',
    'alpha;': 'α',
    'amp': '&',
    'amp;': '&',
    'and;': '∧',
    'ang;': '∠',
    'apos;': "'",
    'aring': 'å',
    'aring;': 'å',
    'asymp;': '≈',
    'atilde': 'ã',
    'atilde;': 'ã',
    'auml': 'ä',
    'auml;': 'ä',
    'bdquo;': '„',
    'beta;': 'β',
    'brvbar': '¦',
    'brvbar;': '¦',
    'bull;': '•',
    'cap;': '∩',
    'ccedil': 'ç',
    'ccedil;': 'ç',
    'cedil': '¸',
    'cedil;': '¸',
    'cent': '¢',
    'cent;': '¢',
    'chi;': 'χ',
    'circ;': 'ˆ',
    'clubs;': '♣',
    'cong;': '≅',
    'copy': '©',
    'copy;': '©',
    'crarr;': '↵',
    'cup;': '∪',
    'curren': '¤',
    'curren;': '¤',
    'dArr;': '⇓',
    'dagger;': '†',
    'darr;': '↓',
    'deg': '°',
    'deg;': '°',
    'delta;': 'δ',
    'diams;': '♦',
    'divide': '÷',
    'divide;': '÷',
    'eacute': 'é',
    'eacute;': 'é',
    'ecirc': 'ê',
    'ecirc;': 'ê',
    'egrave': 'è',
    'egrave;': 'è',
    'empty;': '∅',
    'emsp;': '\u2003',
    'ensp;': '\u2002',
    'epsilon;': 'ε',
    'equiv;': '≡',
    'eta;': 'η',
    'eth': 'ð',
    'eth;': 'ð',
    'euml': 'ë',
    'euml;': 'ë',
    'euro;': '€',
    'exist;': '∃',
    'fnof;': 'ƒ',
    'forall;': '∀',
    'frac12': '½',
    'frac12;': '½',
    'frac14': '¼',
    'frac14;': '¼',
    'frac34': '¾',
    'frac34;': '¾',
    'frasl;': '⁄',
    'gamma;': 'γ',
    'ge;': '≥',
    'gt': '>',
    'gt;': '>',
    'hArr;': '⇔',
    'harr;': '↔',
    'hearts;': '♥',
    'hellip;': '…',
    'iacute': 'í',
    'iacute;': 'í',
    'icirc': 'î',
    'icirc;': 'î',
    'iexcl': '¡',
    'iexcl;': '¡',
    'igrave': 'ì',
    'igrave;': 'ì',
    'image;': 'ℑ',
    'infin;': '∞',
    'int;': '∫',
    'iota;': 'ι',
    'iquest': '¿',
    'iquest;': '¿',
    'isin;': '∈',
    'iuml': 'ï',
    'iuml;': 'ï',
    'kappa;': 'κ',
    'lArr;': '⇐',
    'lambda;': 'λ',
    'lang;': '⟨',
    'laquo': '«',
    'laquo;': '«',
    'larr;': '←',
    'lceil;': '⌈',
    'ldquo;': '“',
    'le;': '≤',
    'lfloor;': '⌊',
    'lowast;': '∗',
    'loz;': '◊',
    'lrm;': '\u200e',
    'lsaquo;': '‹',
    'lsquo;': '‘',
    'lt': '<',
    'lt;': '<',
    'macr': '¯',
    'macr;': '¯',
    'mdash;': '—',
    'micro': 'µ',
    'micro;': 'µ',
    'middot': '·',
    'middot;': '·',
    'minus;': '−',
    'mu;': 'μ',
    'nabla;': '∇',
    'nbsp': '\xa0',
    'nbsp;': '\xa0',
    'ndash;': '–',
    'ne;': '≠',
    'ni;': '∋',
    'not': '¬',
    'not;': '¬',
    'notin;': '∉',
    'nsub;': '⊄',
    'ntilde': 'ñ',
    'ntilde;': 'ñ',
    'nu;': 'ν',
    'oacute': 'ó',
    'oacute;': 'ó',
    'ocirc': 'ô',
    'ocirc;': 'ô',
    'oelig;': 'œ',
    'ograve': 'ò',
    'ograve;': 'ò',
    'oline;': '‾',
    'omega;': 'ω',
    'omicron;': 'ο',
    'oplus;': '⊕',
    'or;': '∨',
    'ordf': 'ª',
    'ordf;': 'ª',
    'ordm': 'º',
    'ordm;': 'º',
    'oslash': 'ø',
    'oslash;': 'ø',
    'otilde': 'õ',
    'otilde;': 'õ',
    'otimes;': '⊗',
    'ouml': 'ö',
    'ouml;': 'ö',
    'para': '¶',
    'para;': '¶',
    'part;': '∂',
    'permil;': '‰',
    'perp;': '⊥',
    'phi;': 'φ',
    'pi;': 'π',
    'piv;': 'ϖ',
    'plusmn': '±',
    'plusmn;': '±',
    'pound': '£',
    'pound;': '£',
    'prime;': '′',
    'prod;': '∏',
    'prop;': '∝',
    'psi;': 'ψ',
    'quot': '"',
    'quot;': '"',
    'rArr;': '⇒',
    'radic;': '√',
    'rang;': '⟩',
    'raquo': '»',
    'raquo;': '»',
    'rarr;': '→',
    'rceil;': '⌉',
    'rdquo;': '”',
    'real;': 'ℜ',
    'reg': '®',
    'reg;': '®',
    'rfloor;': '⌋',
    'rho;': 'ρ',
    'rlm;': '\u200f',
    'rsaquo;': '›',
    'rsquo;': '’',
    'sbquo;': '‚',
    'scaron;': 'š',
    'sdot;': '⋅',
    'sect': '§',
    'sect;': '§',
    'shy': '\xad',
    'shy;': '\xad',
    'sigma;': 'σ',
    'sigmaf;': 'ς',
    'sim;': '∼',
    'spades;': '♠',
    'sub;': '⊂',
    'sube;': '⊆',
    'sum;': '∑',
    'sup1': '¹',
    'sup1;': '¹',
    'sup2': '²',
    'sup2;': '²',
    'sup3': '³',
    'sup3;': '³',
    'sup;': '⊃',
    'supe;': '⊇',
    'szlig': 'ß',
    'szlig;': 'ß',
    'tau;': 'τ',
    'there4;': '∴',
    'theta;': 'θ',
    'thetasym;': 'ϑ',
    'thinsp;': '\u2009',
    'thorn': 'þ',
    'thorn;': 'þ',
    'tilde;': '˜',
    'times': '×',
    'times;': '×',
    'trade;': '™',
    'uArr;': '⇑',
    'uacute': 'ú',
    'uacute;': 'ú',
    'uarr;': '↑',
    'ucirc': 'û',
    'ucirc;': 'û',
    'ugrave': 'ù',
    'ugrave;': 'ù',
    'uml': '¨',
    'uml;': '¨',
    'upsih;': 'ϒ',
    'upsilon;': 'υ',
    'uuml': 'ü',
    'uuml;': 'ü',
    'weierp;': '℘',
    'xi;': 'ξ',
    'yacute': 'ý',
    'yacute;': 'ý',
    'yen': '¥',
    'yen;': '¥',
    'yuml': 'ÿ',
    'yuml;': 'ÿ',
    'zeta;': 'ζ',
    'zwj;': '\u200d',
    'zwnj;': '\u200c',
}

_invalid_charrefs = {
    0x00: '\ufffd',  # REPLACEMENT CHARACTER
    0x0d: '\r',      # CARRIAGE RETURN
    0x80: '\u20ac',  # EURO SIGN
    0x81: '\x81',    # <control>
    0x82: '\u201a',  # SINGLE LOW-9 QUOTATION MARK
    0x83: '\u0192',  # LATIN SMALL LETTER F WITH HOOK
    0x84: '\u201e',  # DOUBLE LOW-9 QUOTATION MARK
    0x85: '\u2026',  # HORIZONTAL ELLIPSIS
    0x86: '\u2020',  # DAGGER
    0x87: '\u2021',  # DOUBLE DAGGER
    0x88: '\u02c6',  # MODIFIER LETTER CIRCUMFLEX ACCENT
    0x89: '\u2030',  # PER MILLE SIGN
    0x8a: '\u0160',  # LATIN CAPITAL LETTER S WITH CARON
    0x8b: '\u2039',  # SINGLE LEFT-POINTING ANGLE QUOTATION MARK
    0x8c: '\u0152',  # LATIN CAPITAL LIGATURE OE
    0x8d: '\x8d',    # <control>
    0x8e: '\u017d',  # LATIN CAPITAL LETTER Z WITH CARON
    0x8f: '\x8f',    # <control>
    0x90: '\x90',    # <control>
    0x91: '\u2018',  # LEFT SINGLE QUOTATION MARK
    0x92: '\u2019',  # RIGHT SINGLE QUOTATION MARK
    0x93: '\u201c',  # LEFT DOUBLE QUOTATION MARK
    0x94: '\u201d',  # RIGHT DOUBLE QUOTATION MARK
    0x95: '\u2022',  # BULLET
    0x96: '\u2013',  # EN DASH
    0x97: '\u2014',  # EM DASH
    0x98: '\u02dc',  # SMALL TILDE
    0x99: '\u2122',  # TRADE MARK SIGN
    0x9a: '\u0161',  # LATIN SMALL LETTER S WITH CARON
    0x9b: '\u203a',  # SINGLE RIGHT-POINTING ANGLE QUOTATION MARK
    0x9c: '\u0153',  # LATIN SMALL LIGATURE OE
    0x9d: '\x9d',    # <control>
    0x9e: '\u017e',  # LATIN SMALL LETTER Z WITH CARON
    0x9f: '\u0178',  # LATIN CAPITAL LETTER Y WITH DIAERESIS
}

_invalid_codepoints = {
    # 0x0001 to 0x0008
    0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8,
    # 0x000E to 0x001F
    0xe, 0xf, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19,
    0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f,
    # 0x007F to 0x009F
    0x7f, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8a,
    0x8b, 0x8c, 0x8d, 0x8e, 0x8f, 0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96,
    0x97, 0x98, 0x99, 0x9a, 0x9b, 0x9c, 0x9d, 0x9e, 0x9f,
    # 0xFDD0 to 0xFDEF
    0xfdd0, 0xfdd1, 0xfdd2, 0xfdd3, 0xfdd4, 0xfdd5, 0xfdd6, 0xfdd7, 0xfdd8,
    0xfdd9, 0xfdda, 0xfddb, 0xfddc, 0xfddd, 0xfdde, 0xfddf, 0xfde0, 0xfde1,
    0xfde2, 0xfde3, 0xfde4, 0xfde5, 0xfde6, 0xfde7, 0xfde8, 0xfde9, 0xfdea,
    0xfdeb, 0xfdec, 0xfded, 0xfdee, 0xfdef,
    # others
    0xb, 0xfffe, 0xffff, 0x1fffe, 0x1ffff, 0x2fffe, 0x2ffff, 0x3fffe, 0x3ffff,
    0x4fffe, 0x4ffff, 0x5fffe, 0x5ffff, 0x6fffe, 0x6ffff, 0x7fffe, 0x7ffff,
    0x8fffe, 0x8ffff, 0x9fffe, 0x9ffff, 0xafffe, 0xaffff, 0xbfffe, 0xbffff,
    0xcfffe, 0xcffff, 0xdfffe, 0xdffff, 0xefffe, 0xeffff, 0xffffe, 0xfffff,
    0x10fffe, 0x10ffff
}



def _replace_charref(s):
    if s[0] == '#':
        if s[1] in 'xX':
            num = int(s[2:].rstrip(';'), 16)
        else:
            num = int(s[1:].rstrip(';'))
        if num in _invalid_charrefs:
            return _invalid_charrefs[num]
        if 0xD800 <= num <= 0xDFFF or num > 0x10FFFF:
            return '\uFFFD'
        if num in _invalid_codepoints:
            return ''
        return chr(num)
    if s in _html5:
        return _html5[s]
    # The longest name the reference starts with, as the standard defines it.
    for x in range(len(s) - 1, 1, -1):
        if s[:x] in _html5:
            return _html5[s[:x]] + s[x:]
    return '&' + s


_charref = _re.compile(r'&(#[0-9]+;?'
                       r'|#[xX][0-9a-fA-F]+;?'
                       r'|[^\t\n\f <&#;]{1,32};?)')


def unescape(s):
    """Convert every named and numeric character reference in s to the character it names."""
    if '&' not in s:
        return s
    parts = []
    last = 0
    for m in _charref.finditer(s):
        parts.append(s[last:m.start()])
        parts.append(_replace_charref(m.group(1)))
        last = m.end()
    parts.append(s[last:])
    return ''.join(parts)
