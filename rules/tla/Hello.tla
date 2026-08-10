---------------------------- MODULE Hello ----------------------------
\* Toolchain smoke test: proves the gitignored tla2tools jar + Java 8 + run-tlc.sh path works end to end.
\* Not a Typhon protocol spec — safe to delete once CommitRecovery is green; kept as a 1-second CI sanity check.
EXTENDS Naturals

VARIABLE x

Init == x = 0
Next == x' = (x + 1) % 3
Inv  == x \in 0..2
=============================================================================
