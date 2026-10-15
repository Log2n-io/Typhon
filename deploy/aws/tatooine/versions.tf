# SWG Tatooine deployment — provider pinning.
#
# Its own state, separate from bench/aws/runner/terraform (08-hosting § 6): the CI runner and the game have different
# lifetimes and different blast radii, and one state file holding both would mean a game deploy could roll back the
# merge gate's infrastructure.
terraform {
  required_version = ">= 1.5"
  required_providers {
    aws = { source = "hashicorp/aws", version = "~> 5.0" }
  }

  # Partial configuration: the bucket and the key come from `terraform init -backend-config=...`, because the key is
  # one per stack (stacks/<name>.tfstate) and a hard-coded one would make every stack share a state file.
  #
  # REMOTE and not local, which is the difference between a stack that can be destroyed and one that leaks: a runner's
  # local state is gone when the job ends, so `destroy` would have nothing to destroy from. Pass -backend=false to
  # validate or format without touching S3.
  #
  # LOCKED, and that is not optional here. The workflows run a create, a destroy and an hourly sweep against the same
  # keys, and the sweep's concurrency group is its own -- so a sweep and a create of one stack can and will overlap.
  # Two Terraform runs writing one state file is how a stack ends up half-recorded and un-destroyable, which is the
  # failure that leaves instances billing with nothing tracking them.
  backend "s3" {
    use_lockfile = true
  }
}

provider "aws" {
  region = var.region

  # Everything this module makes carries these, and the cost controls in cost.tf key on them. A resource that escapes
  # the tags escapes the budget alarm, which is how a forgotten instance becomes a monthly surprise.
  default_tags {
    tags = {
      project = "tatooine"
      tier    = var.persistent_data ? "prod" : "demo"
      stack   = var.name
    }
  }
}
