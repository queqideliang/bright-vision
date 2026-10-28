/** Official community distribution. Forks may change branding; preserve applicable LICENSE/NOTICE. */
export const brand = {
  name: 'Bright Vision',
  nameEn: 'Bright Vision',
  company: '崛亮视觉',
  copyright: '© 2026 崛亮视觉 (Bright Vision)',
  sourceLicense: 'Apache-2.0',
  subtitle: '本地BIM工作台',
  subtitleEn: 'Local BIM workspace',
  monogram: 'BV',
  version: '0.6.0',
};

export function brandPresentation(language: 'zh' | 'en') {
  const name = language === 'en' ? brand.nameEn : brand.name;
  return {
    name,
    title: `${name} · ${brand.company}`,
    subtitle: language === 'en' ? brand.subtitleEn : `${brand.company} · ${brand.subtitle}`,
    copyright: brand.copyright,
    licenseHeading: language === 'en' ? 'Company and license' : '公司与许可',
    licenseSummary: language === 'en'
      ? `Community source: ${brand.sourceLicense}. Third-party components retain their own licenses.`
      : `社区源码采用 ${brand.sourceLicense}；第三方组件保留各自的许可。`,
  };
}
