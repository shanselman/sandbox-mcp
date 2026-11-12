export const FIXTURE_VARIANTS = ['light', 'dark', 'dark-large'];
export const CALCULATOR_VARIANTS = Array.from({ length: 16 }, (_, index) => `calculator-${String(index + 1).padStart(2, '0')}`);
export const SAMPLE_CALCULATOR_VARIANTS = Array.from({ length: 16 }, (_, index) => `sample-calculator-${String(index + 1).padStart(2, '0')}`);
export const VARIANT_PATTERN = '(?:light|dark|dark-large|(?:sample-)?calculator-(?:0[1-9]|1[0-6]))';
export const isVariant = variant => FIXTURE_VARIANTS.includes(variant) || CALCULATOR_VARIANTS.includes(variant) || SAMPLE_CALCULATOR_VARIANTS.includes(variant);
export const sampleCalculatorIndex = variant => SAMPLE_CALCULATOR_VARIANTS.indexOf(variant) + 1;
export const calculatorIndex = variant => CALCULATOR_VARIANTS.indexOf(variant) + 1 || sampleCalculatorIndex(variant);
export function sampleAppearance(variant) {
  const index = sampleCalculatorIndex(variant);
  if (!index) throw new Error('Expected a fixed sample-calculator variant');
  const fixtureLayoutScale = 1 + (Math.floor((index - 1) / 2) % 4) * 0.25;
  return { theme: index % 2 ? 'Light' : 'Dark', fixtureLayoutScale, scaleIsSimulation: fixtureLayoutScale !== 1 };
}
export function batchVariants(batch) {
  const variants = batch?.runs?.map(run => run.variant);
  if (!variants || variants.length < 1 || variants.length > 16 || new Set(variants).size !== variants.length
    || !variants.every(isVariant) || !(variants.every(calculatorIndex)
      || variants.length === 3 && FIXTURE_VARIANTS.every(variant => variants.includes(variant))))
    throw new Error('Expected three fixed fixture variants or one to sixteen distinct Calculator workloads');
  return variants;
}
