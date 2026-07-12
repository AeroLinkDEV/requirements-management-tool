import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  metadataBase: new URL("http://localhost:3000"),
  title: "AeroLink · FMS 3.3 Showcase",
  description: "Interactive local concept for controlled aerospace lifecycle data.",
  openGraph: {
    title: "AeroLink · FMS 3.3 Assurance Showcase",
    description: "One controlled story from SCR to released evidence.",
    images: [{ url: "/og.png", width: 1200, height: 630, alt: "AeroLink FMS 3.3 Assurance Showcase" }],
  },
  twitter: {
    card: "summary_large_image",
    title: "AeroLink · FMS 3.3 Assurance Showcase",
    description: "One controlled story from SCR to released evidence.",
    images: ["/og.png"],
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
