-- restic 0.19.1 for Linux agents (test containers). sha256 from the release's SHA256SUMS.
INSERT INTO software_release (id, product, version, platform, source_url, sha256, is_current, created_at)
VALUES
    ('6b1f7f7e-5d4a-4f7e-9d0e-2a4c1b1f0192', 'restic', '0.19.1', 'linux_amd64',
     'https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_linux_amd64.bz2',
     'f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c', true, now()),
    ('6b1f7f7e-5d4a-4f7e-9d0e-2a4c1b1f0193', 'restic', '0.19.1', 'linux_arm64',
     'https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_linux_arm64.bz2',
     'a5f64aaab53d51e311fa3829124c5b703f2d14cf187d8640b6be3b2b49376465', true, now())
ON CONFLICT (product, version, platform) DO NOTHING;
