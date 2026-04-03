export type FailureDiagnosticItem = {
  label: string;
  value: string;
};

export type FailureDiagnostics = {
  summary: string;
  guidance: string | null;
  details: FailureDiagnosticItem[];
};

const preferredDetailOrder = [
  'Step',
  'Worker ID',
  'SAM',
  'Distinguished Name',
  'Current CN',
  'Desired CN',
  'Attributes',
  'Manager ID',
];

export function parseFailureDiagnostics(message: string | null | undefined): FailureDiagnostics | null {
  if (!message || !message.toLowerCase().includes('active directory')) {
    return null;
  }

  const detailsTokenIndex = message.indexOf(' Details: ');
  const guidanceTokenIndex = message.indexOf(' Next check: ');
  const summary =
    detailsTokenIndex >= 0
      ? message.slice(0, detailsTokenIndex).trim()
      : guidanceTokenIndex >= 0
        ? message.slice(0, guidanceTokenIndex).trim()
        : message.trim();
  const guidance = guidanceTokenIndex >= 0 ? message.slice(guidanceTokenIndex + ' Next check: '.length).trim() : null;

  if (detailsTokenIndex < 0) {
    return { summary, guidance, details: [] };
  }

  const detailsStart = detailsTokenIndex + ' Details: '.length;
  const detailsLength = guidanceTokenIndex > detailsStart ? guidanceTokenIndex - detailsStart : message.length - detailsStart;
  const detailsSegment = message.slice(detailsStart, detailsStart + detailsLength).trim();
  if (!detailsSegment) {
    return { summary, guidance, details: [] };
  }

  return {
    summary,
    guidance,
    details: parseDetails(detailsSegment),
  };
}

function parseDetails(detailsSegment: string): FailureDiagnosticItem[] {
  const regex = /(?:(?<=^)|(?<=\s))(?<key>[A-Za-z]+)=/g;
  const matches = Array.from(detailsSegment.matchAll(regex));
  if (!matches.length) {
    return [{ label: 'Details', value: detailsSegment }];
  }

  const items: FailureDiagnosticItem[] = [];
  for (let index = 0; index < matches.length; index += 1) {
    const current = matches[index];
    const nextIndex = index + 1 < matches.length ? matches[index + 1].index ?? detailsSegment.length : detailsSegment.length;
    const key = current.groups?.key ?? '';
    const valueStart = (current.index ?? 0) + current[0].length;
    const rawValue = detailsSegment.slice(valueStart, nextIndex).trim();
    if (!rawValue) {
      continue;
    }

    items.push({
      label: formatLabel(key),
      value: rawValue,
    });
  }

  return items.sort((left, right) => {
    const leftIndex = preferredDetailOrder.indexOf(left.label);
    const rightIndex = preferredDetailOrder.indexOf(right.label);
    const normalizedLeft = leftIndex >= 0 ? leftIndex : Number.MAX_SAFE_INTEGER;
    const normalizedRight = rightIndex >= 0 ? rightIndex : Number.MAX_SAFE_INTEGER;
    if (normalizedLeft !== normalizedRight) {
      return normalizedLeft - normalizedRight;
    }

    return left.label.localeCompare(right.label);
  });
}

function formatLabel(key: string) {
  switch (key) {
    case 'WorkerId':
      return 'Worker ID';
    case 'SamAccountName':
      return 'SAM';
    case 'DistinguishedName':
      return 'Distinguished Name';
    case 'CurrentCn':
      return 'Current CN';
    case 'DesiredCn':
      return 'Desired CN';
    case 'ManagerId':
      return 'Manager ID';
    default:
      return key;
  }
}
