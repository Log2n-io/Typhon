using Typhon.Engine;

namespace SpaceBattle;

public readonly record struct DamageIntent(EntityId Attacker, EntityId Target);

public readonly record struct WeaponFireResult(
    bool Attempted,
    bool Hit,
    ushort CooldownTicksRemaining);

public readonly record struct DamageResolution(
    bool IsDestroyed,
    uint RemainingHealth,
    uint AppliedDamage,
    int ParticipatingAttackerCount);

public static class CombatRules
{
    public static WeaponFireResult AdvanceWeaponFire(
        ushort cooldownTicksRemaining,
        PositionSnapshot source,
        PositionSnapshot target)
    {
        if (cooldownTicksRemaining > 1)
        {
            return new WeaponFireResult(
                Attempted: false,
                Hit: false,
                CooldownTicksRemaining: (ushort)(cooldownTicksRemaining - 1));
        }

        bool hit = IsWithinRange(source, target, BehaviorRules.WeaponRange);
        return new WeaponFireResult(
            Attempted: true,
            Hit: hit,
            CooldownTicksRemaining: BehaviorRules.WeaponFireIntervalTicks);
    }

    public static bool IsWithinRange(
        PositionSnapshot source,
        PositionSnapshot target,
        float range)
    {
        float deltaX = target.X - source.X;
        float deltaY = target.Y - source.Y;
        float deltaZ = target.Z - source.Z;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ) <= range * range;
    }
}

public static class DamageResolutionRules
{
    public static DamageResolution Resolve(
        uint currentHealth,
        int hitCount,
        int participatingAttackerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hitCount);
        ArgumentOutOfRangeException.ThrowIfNegative(participatingAttackerCount);
        if (participatingAttackerCount > hitCount)
        {
            throw new ArgumentOutOfRangeException(nameof(participatingAttackerCount));
        }

        ulong attemptedDamage = (ulong)hitCount * BehaviorRules.WeaponDamage;
        uint appliedDamage = (uint)Math.Min(attemptedDamage, currentHealth);
        uint remainingHealth = currentHealth - appliedDamage;
        return new DamageResolution(
            IsDestroyed: remainingHealth == 0,
            RemainingHealth: remainingHealth,
            AppliedDamage: appliedDamage,
            ParticipatingAttackerCount: participatingAttackerCount);
    }
}
