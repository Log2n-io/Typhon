#!/usr/bin/env bash
set -euo pipefail
# Launch the production AntHill benchmark on AWS. --down terminates the instance
# when the run finishes; -y skips the confirmation prompt. Run from the repo root.
#
# For the cheap c5d.metal dry run, use bench/aws/anthill-dryrun.sky.yaml instead:
#   sky launch bench/aws/anthill-dryrun.sky.yaml --down -y
sky launch bench/aws/anthill-bench.sky.yaml --down -y "$@"
