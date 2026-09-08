type Span = { kind: string; text: string };

export type RequirementRedlineData = {
  from: number;
  to: number;
  statement: Span[];
  rationale: Span[];
  comparison: {
    isComplete: boolean;
    statement: "Detailed" | "WholeField";
    rationale: "Detailed" | "WholeField";
  };
  verificationChanged: boolean;
  fromVerification: string;
  toVerification: string;
};

export default function RequirementRedline({ data, onClose }: {
  data: RequirementRedlineData;
  onClose: () => void;
}) {
  return (
    <div className="reqModal redlineModal" role="dialog" aria-label="Revision comparison" aria-modal="true">
      <div>
        <button className="modalClose" aria-label="Close comparison" onClick={onClose}>×</button>
        <p className="eyebrow">CONTROLLED REDLINE / REV {data.from} → {data.to}</p>
        <h2>Revision comparison</h2>
        {!data.comparison?.isComplete ? (
          <p role="alert">A complete comparison is unavailable. Close this comparison and open the exact revisions in History to review their full content.</p>
        ) : (
          <>
            {(["statement", "rationale"] as const).map(field => (
              <section key={field} aria-label={field === "statement" ? "Statement" : "Rationale"}>
                <h3>{field === "statement" ? "Statement" : "Rationale"}</h3>
                {data.comparison[field] === "WholeField" && (
                  <p>Full previous and current text are shown for this long field. Individual word changes are not highlighted.</p>
                )}
                <div className="redlineText">
                  {data[field].map((span, index) => (
                    <span className={span.kind} key={index}>{span.text}{" "}</span>
                  ))}
                </div>
              </section>
            ))}
            {data.verificationChanged && (
              <p className="verificationDiff">
                Verification changed: <del>{data.fromVerification}</del> → <ins>{data.toVerification}</ins>
              </p>
            )}
          </>
        )}
      </div>
    </div>
  );
}
