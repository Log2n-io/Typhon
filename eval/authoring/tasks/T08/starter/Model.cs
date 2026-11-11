// DO NOT MODIFY — given model for this task.
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Eval;

[Component("Eval.Balance", 1)]
public struct Balance
{
    public long Amount;
}

[Archetype]
public sealed partial class Account : Archetype<Account>
{
    public static readonly Comp<Balance> Balance = Register<Balance>();
}
