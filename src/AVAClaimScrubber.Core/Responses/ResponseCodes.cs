namespace AVAClaimScrubber.Core.Responses;

/// <summary>
/// The codes payers answer with, worded as X12 publishes them (x12.org's code lists, retrieved 2026-11-24, the usage
/// notes left off): claim adjustment reason codes (CARC), remittance advice remark codes (RARC), and the 277's claim
/// status category and status codes. Only the codes the program explains or replays are here; any other is shown by
/// its number.
/// </summary>
public static class ResponseCodes
{
    public const string Source = "X12 code lists (x12.org): CARC, RARC, Claim Status Category and Claim Status codes, retrieved 2026-11-24";

    public static readonly IReadOnlyDictionary<string, string> Carc = new Dictionary<string, string>
    {
        ["1"] = "Deductible Amount",
        ["2"] = "Coinsurance Amount",
        ["3"] = "Co-payment Amount",
        ["4"] = "The procedure code is inconsistent with the modifier used.",
        ["5"] = "The procedure code/type of bill is inconsistent with the place of service.",
        ["6"] = "The procedure/revenue code is inconsistent with the patient's age.",
        ["7"] = "The procedure/revenue code is inconsistent with the patient's gender.",
        ["9"] = "The diagnosis is inconsistent with the patient's age.",
        ["10"] = "The diagnosis is inconsistent with the patient's gender.",
        ["11"] = "The diagnosis is inconsistent with the procedure.",
        ["16"] = "Claim/service lacks information or has submission/billing error(s).",
        ["18"] = "Exact duplicate claim/service (Use only with Group Code OA except where state workers' compensation regulations requires CO)",
        ["22"] = "This care may be covered by another payer per coordination of benefits.",
        ["23"] = "The impact of prior payer(s) adjudication including payments and/or adjustments. (Use only with Group Code OA)",
        ["24"] = "Charges are covered under a capitation agreement/managed care plan.",
        ["26"] = "Expenses incurred prior to coverage.",
        ["27"] = "Expenses incurred after coverage terminated.",
        ["29"] = "The time limit for filing has expired.",
        ["31"] = "Patient cannot be identified as our insured.",
        ["45"] = "Charge exceeds fee schedule/maximum allowable or contracted/legislated fee arrangement.",
        ["49"] = "This is a non-covered service because it is a routine/preventive exam or a diagnostic/screening procedure done in conjunction with a routine/preventive exam.",
        ["50"] = "These are non-covered services because this is not deemed a 'medical necessity' by the payer.",
        ["55"] = "Procedure/treatment/drug is deemed experimental/investigational by the payer.",
        ["58"] = "Treatment was deemed by the payer to have been rendered in an inappropriate or invalid place of service.",
        ["59"] = "Processed based on multiple or concurrent procedure rules. (For example multiple surgery or diagnostic imaging, concurrent anesthesia.)",
        ["96"] = "Non-covered charge(s).",
        ["97"] = "The benefit for this service is included in the payment/allowance for another service/procedure that has already been adjudicated.",
        ["109"] = "Claim/service not covered by this payer/contractor. You must send the claim/service to the correct payer/contractor.",
        ["119"] = "Benefit maximum for this time period or occurrence has been reached.",
        ["146"] = "Diagnosis was invalid for the date(s) of service reported.",
        ["151"] = "Payment adjusted because the payer deems the information submitted does not support this many/frequency of services.",
        ["167"] = "This (these) diagnosis(es) is (are) not covered.",
        ["170"] = "Payment is denied when performed/billed by this type of provider.",
        ["181"] = "Procedure code was invalid on the date of service.",
        ["182"] = "Procedure modifier was invalid on the date of service.",
        ["185"] = "The rendering provider is not eligible to perform the service billed.",
        ["197"] = "Precertification/authorization/notification/pre-treatment absent.",
        ["204"] = "This service/equipment/drug is not covered under the patient's current benefit plan",
        ["206"] = "National Provider Identifier - missing.",
        ["207"] = "National Provider identifier - Invalid format",
        ["208"] = "National Provider Identifier - Not matched.",
        ["222"] = "Exceeds the contracted maximum number of hours/days/units by this provider for this period. This is not patient specific.",
        ["226"] = "Information requested from the Billing/Rendering Provider was not provided or not provided timely or was insufficient/incomplete.",
        ["234"] = "This procedure is not paid separately.",
        ["236"] = "This procedure or procedure/modifier combination is not compatible with another procedure or procedure/modifier combination provided on the same day according to the National Correct Coding Initiative or workers compensation state regulations/ fee schedule requirements.",
        ["242"] = "Services not provided by network/primary care providers.",
        ["252"] = "An attachment/other documentation is required to adjudicate this claim/service.",
        ["253"] = "Sequestration - reduction in federal payment",
        ["A1"] = "Claim/Service denied.",
        ["B7"] = "This provider was not certified/eligible to be paid for this procedure/service on this date of service.",
        ["B13"] = "Previously paid. Payment for this claim/service may have been provided in a previous payment.",
        ["B15"] = "This service/procedure requires that a qualifying service/procedure be received and covered. The qualifying other service/procedure has not been received/adjudicated.",
        ["B16"] = "'New Patient' qualifications were not met.",
        ["B20"] = "Procedure/service was partially or fully furnished by another provider.",
    };

    public static readonly IReadOnlyDictionary<string, string> Rarc = new Dictionary<string, string>
    {
        ["M20"] = "Missing/incomplete/invalid HCPCS.",
        ["M51"] = "Missing/incomplete/invalid procedure code(s).",
        ["M76"] = "Missing/incomplete/invalid diagnosis or condition.",
        ["MA04"] = "Secondary payment cannot be considered without the identity of or payment information from the primary payer. The information was either not reported or was illegible.",
        ["MA130"] = "Your claim contains incomplete and/or invalid information, and no appeal rights are afforded because the claim is unprocessable. Please submit a new claim with the complete/correct information.",
        ["N54"] = "Claim information is inconsistent with pre-certified/authorized services.",
        ["N56"] = "Procedure code billed is not correct/valid for the services billed or the date of service billed.",
        ["N115"] = "This decision was based on a Local Coverage Determination (LCD). An LCD provides a guide to assist in determining whether a particular item or service is covered. A copy of this policy is available at www.cms.gov/mcd, or if you do not have web access, you may contact the contractor to request a copy of the LCD.",
        ["N130"] = "Consult plan benefit documents/guidelines for information about restrictions for this service.",
        ["N211"] = "Alert: You may not appeal this decision.",
        ["N290"] = "Missing/incomplete/invalid rendering provider primary identifier.",
        ["N362"] = "The number of Days or Units of Service exceeds our acceptable maximum.",
        ["N657"] = "This should be billed with the appropriate code for these services.",
    };

    public static readonly IReadOnlyDictionary<string, string> StatusCategory = new Dictionary<string, string>
    {
        ["A0"] = "Acknowledgement/Forwarded-The claim/encounter has been forwarded to another entity.",
        ["A1"] = "Acknowledgement/Receipt-The claim/encounter has been received. This does not mean that the claim has been accepted for adjudication.",
        ["A2"] = "Acknowledgement/Acceptance into adjudication system-The claim/encounter has been accepted into the adjudication system.",
        ["A3"] = "Acknowledgement/Returned as unprocessable claim-The claim/encounter has been rejected and has not been entered into the adjudication system.",
        ["A4"] = "Acknowledgement/Not Found-The claim/encounter can not be found in the adjudication system.",
        ["A6"] = "Acknowledgement/Rejected for Missing Information - The claim/encounter is missing the information specified in the Status details and has been rejected.",
        ["A7"] = "Acknowledgement/Rejected for Invalid Information - The claim/encounter has invalid information as specified in the Status details and has been rejected.",
        ["A8"] = "Acknowledgement/Rejected for relational field in error.",
        ["F1"] = "Finalized/Payment-The claim/line has been paid.",
        ["F2"] = "Finalized/Denial-The claim/line has been denied.",
    };

    public static readonly IReadOnlyDictionary<string, string> Status = new Dictionary<string, string>
    {
        ["19"] = "Entity acknowledges receipt of claim/encounter.",
        ["20"] = "Accepted for processing.",
        ["21"] = "Missing or invalid information.",
        ["32"] = "Subscriber and policy number/contract number not found.",
        ["33"] = "Subscriber and subscriber id not found.",
        ["35"] = "Claim/encounter not found.",
        ["145"] = "Entity's specialty/taxonomy code.",
        ["164"] = "Entity's contract/member number.",
        ["178"] = "Total Claim Charge Amount",
        ["187"] = "Date(s) of service.",
        ["255"] = "Diagnosis code.",
        ["400"] = "Claim is out of balance",
        ["453"] = "Procedure Code Modifier(s) for Service(s) Rendered",
        ["454"] = "Procedure code for services rendered.",
        ["507"] = "HCPCS",
        ["510"] = "Future date.",
        ["511"] = "Invalid character.",
        ["562"] = "Entity's National Provider Identifier (NPI).",
    };

    /// <summary>The 835's claim adjustment group codes (CAS01).</summary>
    public static readonly IReadOnlyDictionary<string, string> Group = new Dictionary<string, string>
    {
        ["CO"] = "Contractual obligation",
        ["CR"] = "Correction and reversal",
        ["OA"] = "Other adjustment",
        ["PI"] = "Payer initiated reduction",
        ["PR"] = "Patient responsibility",
    };

    public static string Describe(IReadOnlyDictionary<string, string> list, string code) =>
        list.TryGetValue(code, out var text) ? text : "(not in the built-in list)";
}
