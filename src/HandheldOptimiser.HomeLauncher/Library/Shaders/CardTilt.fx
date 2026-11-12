// The Old School layout's Console space look: a card turned a little away from the player, its far (right)
// edge shorter than its near one, as a picture in true perspective is. A projective mapping that WPF's own
// 2D transforms cannot draw. Compiled to CardTilt.ps with the Windows SDK's fxc, which the build does not
// need: from this folder, fxc /nologo /T ps_2_0 /E main /Fo CardTilt.ps CardTilt.fx

sampler2D input : register(s0);

// How much further away the far edge is than the near one: 0 is flat, 0.25 makes it four fifths as tall.
float tilt : register(c0);

// How bright the card is drawn, 1 for as it is, less for a card further back.
float dim : register(c1);

// Where the card sits in the picture the effect is given, which has room around it: its left edge and
// its width across, and the line through its middle, each as a part of the whole from 0 to 1.
float cardLeft : register(c2);
float cardWidth : register(c3);
float cardMiddle : register(c4);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // Across the card, 0 at its near edge and 1 at its far one.
    float x = (uv.x - cardLeft) / cardWidth;

    // Which part of the flat card is seen at this point once it is turned, and how much nearer the
    // middle line it is drawn there.
    float u = x / (1 + tilt - x * tilt);
    float w = 1 + tilt * u;
    float v = cardMiddle + (uv.y - cardMiddle) * w;

    float4 colour = tex2D(input, float2(cardLeft + u * cardWidth, v));
    colour.rgb *= dim;
    return colour;
}
