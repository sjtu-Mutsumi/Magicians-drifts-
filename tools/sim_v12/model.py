from collections import Counter
from dataclasses import dataclass, field
from fractions import Fraction as Q
import math
import random
from copy import deepcopy
from .catalog import CARDS, ELEMENTS

def ceil(x):
    return math.ceil(x)

@dataclass(frozen=True)
class Instance:
    uid: str
    name: str

    def __deepcopy__(self,memo):
        return self  # immutable identity; zones, not instances, are mutable

@dataclass(frozen=True)
class PlannedCard:
    uid: str
    name: str
    cost: int
    choices: tuple = ()  # (purpose, tuple(instance IDs)) for each private choice

@dataclass
class Modifier:
    kind: str
    value: Q
    owner: int
    expires: int
    stamp: tuple = ()

@dataclass
class Fighter:
    seat: int
    deck: tuple
    specialization: str = ''
    seed: int = 0
    hp: int = 1000
    max_hp: int = 1000
    attack: Q = Q(100)
    defense: Q = Q(40)
    shield: int = 0
    temporary: list = field(default_factory=list)  # [amount, expiry stage, conversion ratio]
    layers: dict = field(default_factory=lambda: {e: 0 for e in ELEMENTS})
    active: set = field(default_factory=set)
    seen: set = field(default_factory=set)
    pile: list = field(default_factory=list)
    hand: list = field(default_factory=list)
    discard: list = field(default_factory=list)
    removed: list = field(default_factory=list)
    resolving: list = field(default_factory=list)
    discounts: dict = field(default_factory=dict)
    drawn: set = field(default_factory=set)
    fx: Counter = field(default_factory=Counter)
    pending: Counter = field(default_factory=Counter)
    mods: list = field(default_factory=list)
    subs: list = field(default_factory=list)
    permanent_subs: list = field(default_factory=list)
    counts: Counter = field(default_factory=Counter)
    stage: int = 0
    played: int = 0
    spent: int = 0
    energy: int = 0
    next_fire: int = 100
    last_fire: int = -1
    last_main: int = 0
    poison: int = 0
    poison_start: int = 0
    poison_due: int = 10**12
    wind_marks: int = 0
    tide: int = 0
    spent_tide: int = 0
    ember: int = 0
    spent_ember: int = 0
    rewind: tuple = ()
    revived: bool = False
    guard_used: bool = False
    guard_time: int = -1
    shield_events: list = field(default_factory=list)
    known_top: list = field(default_factory=list)  # revealed own topdeck UIDs only

    def __deepcopy__(self,memo):
        clone=object.__new__(type(self));memo[id(self)]=clone
        flat_lists={'pile','hand','discard','removed','resolving','known_top','subs','permanent_subs'}
        for key,value in self.__dict__.items():
            if key=='rng':
                result=random.Random(0);result.setstate(value.getstate())
            elif key in flat_lists:result=list(value)
            elif key in ('deck','instances','rewind'):result=value
            elif key=='temporary':result=[list(batch) for batch in value]
            elif isinstance(value,set):result=set(value)
            elif key in ('layers','discounts','counts'):result=value.copy()
            else:result=deepcopy(value,memo)
            setattr(clone,key,result)
        return clone

    def __post_init__(self):
        self.rng = random.Random(self.seed)
        self.instances = tuple(Instance(f'{self.seat}:{i}', n) for i, n in enumerate(self.deck))
        if not self.pile:
            self.pile = list(self.instances)
            self.rng.shuffle(self.pile)

    def has(self, name):
        return name in self.active

    @property
    def shield_total(self):
        return self.shield + sum(x[0] for x in self.temporary)

    def value(self, kind):
        return sum(m.value for m in self.mods if m.kind == kind)

    @property
    def guarded(self):
        return self.value('negative_guard') > 0

    def assert_instances(self):
        items = self.pile + self.hand + self.discard + self.removed + self.resolving
        assert Counter(x.uid for x in items) == Counter(x.uid for x in self.instances), 'Instance conservation'
        assert self.energy >= 0
        assert self.shield >= 0 and all(x[0] >= 0 for x in self.temporary)
        assert 0 <= self.ember <= 5
