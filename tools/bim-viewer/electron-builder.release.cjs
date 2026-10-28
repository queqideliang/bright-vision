'use strict';

// This configuration is preparation for a real signed release. It never
// supplies a certificate, searches for private keys, or changes Windows trust.
// The ordinary package.json build remains the explicit unsigned preview path.
const { build } = require('./package.json');

module.exports = {
  ...build,
  directories: { ...build.directories, output: 'release/signed' },
  forceCodeSigning: true,
  publish: null,
  win: {
    ...build.win,
    signAndEditExecutable: true,
    signExecutable: true,
  },
};
