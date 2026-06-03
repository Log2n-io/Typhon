// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { SpatialClauseDto } from '@/api/generated/model/spatialClauseDto';
import type { ArchetypeComponent } from '@/hooks/queryConsole/useArchetypeComponents';

// SpatialChip (#386) reads two schema hooks. Mock both so the test is a pure render of the chip's editor logic.
const mocks = vi.hoisted(() => ({
  components: [] as ArchetypeComponent[],
}));
vi.mock('@/hooks/queryConsole/useArchetypeComponents', () => ({
  useArchetypeComponents: () => ({ components: mocks.components, isLoading: false }),
}));
vi.mock('@/hooks/queryConsole/useComponentNames', () => ({
  useComponentNames: () => ({ label: (n: string | null | undefined) => n ?? '', isLoading: false }),
}));

import { SpatialChip } from '@/panels/QueryConsole/chips/SpatialChip';

const comp = (typeName: string, hasSpatialIndex: boolean): ArchetypeComponent => ({
  typeName,
  fullName: `Fix.${typeName}`,
  indexCount: 0,
  fieldCount: 1,
  hasSpatialIndex,
});

afterEach(cleanup);

describe('SpatialChip', () => {
  it('disables the add button until an archetype is selected', () => {
    mocks.components = [];
    render(<SpatialChip value={null} archetype={null} onChange={() => {}} />);
    expect((screen.getByRole('button', { name: /spatial/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('enables the add button once an archetype is selected', () => {
    mocks.components = [comp('Pos', true)];
    render(<SpatialChip value={null} archetype="#823" onChange={() => {}} />);
    expect((screen.getByRole('button', { name: /spatial/i }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('renders one numeric input per AABB parameter, pre-filled from the clause', () => {
    const value: SpatialClauseDto = { component: 'Pos', kind: 'aabb', parameters: [1, 2, 3, 4, 5, 6] };
    render(<SpatialChip value={value} archetype="#823" onChange={() => {}} />);
    const inputs = screen.getAllByRole('spinbutton');
    expect(inputs).toHaveLength(6); // minX, minY, minZ, maxX, maxY, maxZ
    expect(inputs.map((i) => (i as HTMLInputElement).value)).toEqual(['1', '2', '3', '4', '5', '6']);
    expect((screen.getByLabelText('Spatial kind') as HTMLSelectElement).value).toBe('aabb');
  });

  it('switching kind resets parameters to the new kind’s zeroed shape', () => {
    const onChange = vi.fn();
    const value: SpatialClauseDto = { component: 'Pos', kind: 'aabb', parameters: [1, 2, 3, 4, 5, 6] };
    render(<SpatialChip value={value} archetype="#823" onChange={onChange} />);
    fireEvent.change(screen.getByLabelText('Spatial kind'), { target: { value: 'nearby' } });
    expect(onChange).toHaveBeenCalledWith({ component: 'Pos', kind: 'nearby', parameters: [0, 0, 0, 0] });
  });

  it('editing a parameter updates only that index', () => {
    const onChange = vi.fn();
    const value: SpatialClauseDto = { component: 'Pos', kind: 'nearby', parameters: [0, 0, 0, 0] };
    render(<SpatialChip value={value} archetype="#823" onChange={onChange} />);
    const radius = screen.getByLabelText('NEARBY radius');
    fireEvent.change(radius, { target: { value: '50' } });
    expect(onChange).toHaveBeenCalledWith({ component: 'Pos', kind: 'nearby', parameters: [0, 0, 0, 50] });
  });

  it('removing the clause emits null', () => {
    const onChange = vi.fn();
    const value: SpatialClauseDto = { component: 'Pos', kind: 'nearby', parameters: [0, 0, 0, 0] };
    render(<SpatialChip value={value} archetype="#823" onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /remove spatial/i }));
    expect(onChange).toHaveBeenCalledWith(null);
  });
});
