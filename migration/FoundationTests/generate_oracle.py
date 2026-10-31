"""CPython 3.12-only offline reference vectors; not shipped with the native game."""
from pathlib import Path
from fractions import Fraction
import hashlib
import json
import math
import random
import struct
import sys

if sys.version_info[:2] != (3, 12):
    raise RuntimeError('Generate reference vectors with the original CPython 3.12 runtime')

output = {'rng': [], 'fractions': [], 'float_fractions': [], 'fraction_floats': [], 'salts': []}
for seed in [0, 1, -7, 2**32 + 1, 2**64 - 1, 2**127 + 12345]:
    rng = random.Random(seed)
    row = {'seed': str(seed), 'random': [rng.random() for _ in range(20)]}
    row['bits'] = [{'k': k, 'value': str(rng.getrandbits(k))} for k in [0, 1, 5, 32, 33, 64, 129]]
    row['ranges'] = [{'n': n, 'value': rng.randrange(n)} for n in [1, 2, 8, 33, 1024, 2, 17, 999999]]
    shuffled = list(range(40))
    rng.shuffle(shuffled)
    row['shuffle'] = shuffled
    row['samples'] = [{'n': n, 'k': k, 'value': rng.sample(range(n), k)} for n, k in [(10, 8), (100, 3), (100, 12), (500, 30)]]
    row['choices'] = rng.choices(['a', 'b', 'c', 'd'], [0, 0.1, 0.4, 0.5], k=30)
    rng = random.Random(seed)
    row['long_words_sha256'] = hashlib.sha256(b''.join(rng.getrandbits(32).to_bytes(4, 'little') for _ in range(20000))).hexdigest()
    output['rng'].append(row)

for a, b in [(Fraction(2, 3), Fraction(3, 7)), (Fraction(-7, 3), Fraction(5, 2)),
             (Fraction(5, 2), Fraction(-3, 4)), (Fraction(10**80 + 1, 11), Fraction(10**65 + 7, 17)),
             (Fraction(-1, 1000), Fraction(-1, 20))]:
    output['fractions'].append(dict(a=str(a), b=str(b), add=str(a+b), mul=str(a*b), div=str(a/b), mod=str(a%b),
                                    floor=str(math.floor(a)), ceil=str(math.ceil(a)), pow=str(a**-3)))
for value in [0.1, -0.3, 1.1, 0.0, 5e-324, 1e100]:
    output['float_fractions'].append(dict(value=value, fraction=str(Fraction(value))))

rng = random.Random(1987)
values = [Fraction(1, 10), Fraction(2**1074+1, 2**1074), Fraction(1, 2**1075),
          Fraction(3, 2**1075), Fraction(2**53+1, 2**53), Fraction(10**400+1, 10**400-1), Fraction(-1, 2**1075)]
for _ in range(500):
    numerator = rng.getrandbits(rng.randrange(1, 1000)) + 1
    denominator = rng.getrandbits(rng.randrange(1, 1000)) + 1
    values.append(Fraction(numerator * (-1 if rng.random() < .5 else 1), denominator))
output['fraction_floats'] = [{'fraction': str(value), 'bits': struct.unpack('<q', struct.pack('<d', float(value)))[0]} for value in values]

for seed in [1, -7, 2**64+123]:
    for deck in [(), ('引火',), ('护盾', '短促', '炽焰王冠'), ("a'b", 'a"b', 'a\n\t\\')]:
        salt = int.from_bytes(hashlib.sha256(repr((seed, tuple(deck))).encode()).digest()[:8], 'big')
        output['salts'].append(dict(seed=str(seed), deck=deck, salt=str(salt)))

Path(__file__).with_name('oracle.json').write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding='utf-8')
print('FOUNDATION_ORACLE_READY')
