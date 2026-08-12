namespace SpaceBattle;

public readonly record struct MovementStep(PositionSnapshot Position, MotionSnapshot Motion);

public static class MovementRules
{
    public static MovementStep Advance(
        PositionSnapshot position,
        MotionSnapshot motion,
        float simulationDeltaSeconds,
        float worldSize)
    {
        if (!float.IsFinite(worldSize) || worldSize <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(worldSize));
        }

        if (!float.IsFinite(simulationDeltaSeconds) || simulationDeltaSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(simulationDeltaSeconds));
        }

        if (!float.IsFinite(motion.Speed) || motion.Speed < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(motion));
        }

        var normalizedMotion = NormalizeDirection(motion);
        var x = AdvanceAxis(position.X, normalizedMotion.DirectionX, normalizedMotion.Speed, simulationDeltaSeconds, worldSize);
        var y = AdvanceAxis(position.Y, normalizedMotion.DirectionY, normalizedMotion.Speed, simulationDeltaSeconds, worldSize);
        var z = AdvanceAxis(position.Z, normalizedMotion.DirectionZ, normalizedMotion.Speed, simulationDeltaSeconds, worldSize);

        return new MovementStep(
            new PositionSnapshot(x.Position, y.Position, z.Position),
            new MotionSnapshot(x.Direction, y.Direction, z.Direction, normalizedMotion.Speed));
    }

    private static MotionSnapshot NormalizeDirection(MotionSnapshot motion)
    {
        var lengthSquared =
            (motion.DirectionX * motion.DirectionX) +
            (motion.DirectionY * motion.DirectionY) +
            (motion.DirectionZ * motion.DirectionZ);
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(motion));
        }

        var inverseLength = 1f / MathF.Sqrt(lengthSquared);
        return new MotionSnapshot(
            motion.DirectionX * inverseLength,
            motion.DirectionY * inverseLength,
            motion.DirectionZ * inverseLength,
            motion.Speed);
    }

    private static AxisStep AdvanceAxis(
        float position,
        float direction,
        float speed,
        float simulationDeltaSeconds,
        float worldSize)
    {
        if (!float.IsFinite(position) || !float.IsFinite(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var nextPosition = position + (direction * speed * simulationDeltaSeconds);
        var nextDirection = direction;

        while (nextPosition < 0f || nextPosition > worldSize)
        {
            if (nextPosition < 0f)
            {
                nextPosition = -nextPosition;
            }
            else
            {
                nextPosition = (2f * worldSize) - nextPosition;
            }

            nextDirection = -nextDirection;
        }

        if ((nextPosition == 0f && nextDirection < 0f) ||
            (nextPosition == worldSize && nextDirection > 0f))
        {
            nextDirection = -nextDirection;
        }

        return new AxisStep(nextPosition, nextDirection);
    }

    private readonly record struct AxisStep(float Position, float Direction);
}
