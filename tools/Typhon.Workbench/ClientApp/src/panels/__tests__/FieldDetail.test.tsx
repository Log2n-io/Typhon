// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ComponentSchema } from '@/hooks/schema/types';
import { useSelectionStore } from '@/stores/useSelectionStore';

// The field leaf card (FieldDetail) shows its OWNING component on its own line, as the friendly short name (the
// same smart label the Schema Explorer / File Map use), with the full name on hover. This is the jsdom-friendly
// proof of that wiring; the rest of the field card is covered by the load-a-file E2E.

const SCHEMA: ComponentSchema = {
  typeName: 'Typhon.ARPG.Schema.Combat.StatusEffects',
  fullName: 'Typhon.ARPG.Schema.Combat.StatusEffects, Typhon.ARPG.Schema, Version=1.0.0.0',
  storageSize: 16,
  totalSize: 16,
  allowMultiple: false,
  revision: 1,
  storageMode: 'Versioned',
  fields: [
    { name: 'Stacks', typeName: 'Int32', typeFullName: 'System.Int32', offset: 0, size: 4, fieldId: 1, isIndexed: false, indexAllowsMultiple: false },
  ],
};

vi.mock('@/hooks/schema/useComponentSchema', () => ({
  useComponentSchema: () => ({ schema: SCHEMA, isLoading: false, isError: false }),
}));
// Smart labeller stub: maps the dotted typeName to its friendly short form, like buildComponentNameMap does.
vi.mock('@/hooks/queryConsole/useComponentNames', () => ({
  useComponentNames: () => ({ label: (n: string) => (n === SCHEMA.typeName ? 'StatusEffects' : n), isLoading: false }),
}));

import DetailPanel from '@/panels/DetailPanel';

function renderInspector() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <DetailPanel />
    </QueryClientProvider>,
  );
}

afterEach(cleanup);

describe('Inspector — field leaf owning-component label', () => {
  it('shows the friendly short component name on its own line, with the full name on hover', () => {
    useSelectionStore.getState().select('field', { component: SCHEMA.typeName, field: 'Stacks' });
    renderInspector();

    expect(screen.getByText('Stacks')).toBeTruthy(); // the field itself

    const owner = screen.getByTestId('field-owner-component');
    expect(owner.textContent).toBe('StatusEffects'); // friendly, not the dotted typeName
    expect(owner.getAttribute('title')).toBe(SCHEMA.fullName); // full name preserved on hover
  });
});
